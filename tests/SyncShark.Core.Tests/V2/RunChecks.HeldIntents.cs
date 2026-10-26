namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    // Permanent ownership boundaries (a nested pair root or a product-owned folder) retire the pending effect
    // without applying it, calling an endpoint or writing a synced baseline. One informational notice per
    // boundary path explains it, Ignore only dismisses the text, and repeating the run adds nothing.
    private static async Task HeldIntentBoundariesAsync()
    {
        foreach (var boundary in new[] { "nested", "product-owned" })
        {
            using var store = SqliteStateStore.Open(
                Path.Combine(Path.GetTempPath(), "held-boundary-" + Guid.NewGuid().ToString("N")),
                Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            store.MarkFirstSyncDone(fx.Pair.PairId);
            IReadOnlyList<string> owned = boundary == "product-owned" ? new[] { "product-data" } : Array.Empty<string>();
            if (boundary == "nested")
                store.SavePair(new PairDef("nested", "nested",
                    new RootDef("ra-nested", SyncProviders.Local, null, @"C:\a\protected", "id:nested-a"),
                    new RootDef("rb-nested", SyncProviders.Dropbox, "acc", "/nested-b", "id:nested-b"),
                    [], [], true));
            var boundaryPath = boundary == "nested" ? "protected" : "product-data";
            var source = fx.A.Seed(boundaryPath + "/source.txt", "owned content");
            var byPath = boundaryPath + "/one.txt";
            var byDecision = boundaryPath + "/two.txt";
            var byEvidence = boundaryPath + "/three.txt";
            // Only the ordinary retry may be retired with the intent; decision and deletion evidence are not
            // this boundary's to settle.
            store.SaveSyncRetry(fx.Pair.PairId,
                new(byPath, "sync", 4, "earlier failure", DateTimeOffset.UtcNow.AddMinutes(5)));
            store.SaveSyncRetry(fx.Pair.PairId,
                new(byDecision, "decision", 2, "awaiting a decision", DateTimeOffset.UtcNow.AddMinutes(5)));
            store.SaveSyncRetry(fx.Pair.PairId,
                new(byEvidence, "deletion-evidence", 3, "history unavailable", DateTimeOffset.UtcNow.AddMinutes(5)));
            var intents = new[]
            {
                new SyncAction("boundary-path", 5, ActionKind.Update, Side.A, byPath, null, null, null, false, "k-path"),
                new SyncAction("boundary-decision", 5, ActionKind.Update, Side.A, byDecision, null, null, null, false, "k-decision"),
                new SyncAction("boundary-evidence", 5, ActionKind.Update, Side.A, byEvidence, null, null, null, false, "k-evidence"),
                // The FromPath branch retires too: only the boundary segment decides ownership.
                new SyncAction("boundary-from", 5, ActionKind.Update, Side.A, "elsewhere/other.txt",
                    boundaryPath + "/from.txt", null, null, false, "k-from"),
                // The opposite side's Source branch is equally protected.
                new SyncAction("boundary-source", 5, ActionKind.Update, Side.B, "elsewhere/sourced.txt",
                    null, source, null, false, "k-source"),
            };
            store.RecordIntents(fx.Pair.PairId, intents);
            var run = new SyncRun(store, fx.Executor, fx.Provider, Settings, TimeProvider.System, _ => owned);
            var baselinesBefore = store.Baselines(fx.Pair.PairId).Count;

            var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            Require(store.UnresolvedIntents(fx.Pair.PairId).Count == 0 && store.CountHeldIntents(fx.Pair.PairId) == 0,
                boundary + ": every boundary intent retires without becoming a temporary hold");
            var notices = store.OpenNotices(fx.Pair.PairId).Where(notice => notice.ReasonKey == "permanent-boundary")
                .ToArray();
            Require(notices.Length == 1 && notices[0].Path == boundaryPath && notices[0].Kind == NoticeKind.Info,
                boundary + ": several intents under one boundary path share one informational notice");
            Require(notices[0].Detail.Contains("retired without applying it or marking the item synced") &&
                notices[0].Detail.Contains("nested pair or a product-owned folder"),
                boundary + ": the notice explains the ownership boundary and that nothing was applied");
            Require(notices[0].Detail.Contains("boundary stays protected"),
                boundary + ": the notice states that dismissal does not remove protection");
            Require(NoticeChoices.IsRetryNotice(notices[0]) &&
                NoticeOptions.Options(notices[0]).Select(option => option.Choice).SequenceEqual([NoticeChoices.Ignore]),
                boundary + ": the permanent notice offers Ignore only");
            Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0,
                boundary + ": retirement performs no endpoint effect");
            Require(store.Baselines(fx.Pair.PairId).Count == baselinesBefore &&
                store.Baselines(fx.Pair.PairId).All(row =>
                    !PairPath.IsSelfOrUnder(row.A.Path, boundaryPath) &&
                    !PairPath.IsSelfOrUnder(row.B.Path, boundaryPath)),
                boundary + ": retirement never writes a synced baseline");
            Require(store.SyncRetryAt(fx.Pair.PairId, byPath) is null &&
                store.SyncRetryAt(fx.Pair.PairId, byDecision) is { Kind: "decision", Attempts: 2 } &&
                store.SyncRetryAt(fx.Pair.PairId, byEvidence) is { Kind: "deletion-evidence", Attempts: 3 },
                boundary + ": retirement clears the ordinary retry only, never decision or deletion evidence");
            Require(report.PendingVerification == 0 && report.Uncertain == 0 && report.Failures.Count == 0,
                boundary + ": permanent retirement is neither a temporary hold nor an unconfirmed effect");

            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            Require(store.OpenNotices(fx.Pair.PairId).Count(notice => notice.ReasonKey == "permanent-boundary") == 1 &&
                store.UnresolvedIntents(fx.Pair.PairId).Count == 0,
                boundary + ": repeating the run neither duplicates the notice nor replays the intent");

            var dismissed = await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, notices[0].NoticeId!,
                NoticeChoices.Ignore, CancellationToken.None);
            Require(dismissed.Contains("boundary remains protected") &&
                store.OpenNotices(fx.Pair.PairId).All(notice => notice.ReasonKey != "permanent-boundary") &&
                store.Pair(fx.Pair.PairId)!.Excluded.Count == 0,
                boundary + ": Ignore dismisses the information only; no exclusion or file change");
        }
        Console.WriteLine("PASS HeldIntentBoundaries");
    }

    // M3: the held count merges into the EXISTING local-rehash-pending summary. Both counts are shown, Show remaining
    // lists the parked changes, the notice closes only when both counts are zero, and it never holds a path. A legacy
    // held-intents summary is closed and never raised again.
    private static async Task HeldIntentSummaryAsync()
    {
        using var store = SqliteStateStore.Open(
            Path.Combine(Path.GetTempPath(), "held-summary-" + Guid.NewGuid().ToString("N")),
            Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        store.MarkFirstSyncDone(fx.Pair.PairId);
        var one = fx.A.Seed("hold/one.txt", "one");
        fx.B.Seed(one.Path, "one");
        var two = fx.A.Seed("hold/two.txt", "two");
        fx.B.Seed(two.Path, "two");
        var three = fx.A.Seed("hold/three.txt", "three");
        fx.B.Seed(three.Path, "three");
        var run = fx.Run();
        // Populate stored rows from the endpoints before creating verification debt.
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        var baselinesBefore = store.Baselines(fx.Pair.PairId).Count;
        var oneLive = store.Live(fx.Pair.A.RootId, [one.Path]).Single();
        var twoLive = store.Live(fx.Pair.A.RootId, [two.Path]).Single();
        var threeLive = store.Live(fx.Pair.A.RootId, [three.Path]).Single();
        var oneHash = one.Hash!;
        var twoHash = two.Hash!;
        var threeHash = three.Hash!;
        store.RequireRehash(fx.Pair.A.RootId, oneLive, "journal lost");
        store.RequireRehash(fx.Pair.A.RootId, twoLive, "journal lost");
        store.RecordIntents(fx.Pair.PairId,
        [
            new SyncAction("held-one", 1, ActionKind.CopyNew, Side.B, one.Path, null,
                oneLive with { Hash = oneHash }, null, false, "held-1"),
            new SyncAction("held-two", 1, ActionKind.CopyNew, Side.B, two.Path, null,
                twoLive with { Hash = twoHash }, null, false, "held-2"),
        ]);
        // A summary written by the previous build is superseded, never left beside the combined one.
        store.AddNotices([new Notice(fx.Pair.PairId, 0, NoticeKind.Info, string.Empty,
            "2 changes are waiting for background verification", "held-intents")]);

        var first = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(store.CountHeldIntents(fx.Pair.PairId) == 2 && store.CountUnresolvedIntents(fx.Pair.PairId) == 2 &&
            store.IsIntentHeld(fx.Pair.PairId, "held-one") && store.IsIntentHeld(fx.Pair.PairId, "held-two"),
            "verification debt keeps both intents unresolved and durably held");
        Require(store.StreamUnresolvedIntentRows(fx.Pair.PairId).All(row => row.State == "held"),
            "the streamed page returns each intent's state without a per-row point lookup");
        var summary = store.OpenNotices(fx.Pair.PairId)
            .Where(notice => notice.ReasonKey == RehashProtection.Reason).ToArray();
        Require(summary.Length == 1 && summary[0].Rule == RehashProtection.Rule &&
            summary[0].Path.Length == 0 && !PlanNotices.HasPathScope(summary[0]),
            "one combined background-verification summary reports the holds and never holds a path");
        Require(summary[0].Detail.Contains("2 files awaiting content verification") &&
            summary[0].Detail.Contains("2 changes are waiting for background verification"),
            "the combined summary carries both counts");
        Require(store.OpenNotices(fx.Pair.PairId).All(notice => notice.ReasonKey != "held-intents"),
            "the legacy held-intents summary is closed, not duplicated");
        Require(NoticeChoices.IsRetryNotice(summary[0]) &&
            NoticeOptions.Options(summary[0]).Select(option => option.Choice).SequenceEqual(
                [NoticeChoices.ShowRemaining, NoticeChoices.RetryNow, NoticeChoices.Ignore]),
            "the combined summary pages, retries and suppresses without ending sync");
        Require(first.PendingVerification == 2 && first.Held == first.PendingVerification && first.Uncertain == 0 &&
            first.Failures.Count == 0,
            "temporary holds are reported as held and pending, never as unconfirmed or failed effects");
        Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0 &&
            await fx.B.ObserveAsync(one.Path, default) is { } oneOnB && oneOnB.Hash == one.Hash &&
            await fx.B.ObserveAsync(two.Path, default) is { } twoOnB && twoOnB.Hash == two.Hash,
            "held intents perform no endpoint effect");
        Require(store.Baselines(fx.Pair.PairId).Count == baselinesBefore,
            "held intents add no synced baseline");

        await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, summary[0].NoticeId!,
            NoticeChoices.RetryNow, CancellationToken.None);
        Require(store.SyncRetryAt(fx.Pair.PairId, string.Empty) is null &&
            store.CountHeldIntents(fx.Pair.PairId) == 2 &&
            store.OpenNotices(fx.Pair.PairId).Count(notice => notice.ReasonKey == RehashProtection.Reason) == 1,
            "summary Retry wakes verification without inventing a root item, clearing debt or duplicating its notice");

        store.AddNotices([new Notice(fx.Pair.PairId, 40, NoticeKind.Info, "", "A pair failure.", "unknown-pair-failure")]);
        var unknownPair = store.OpenNotices(fx.Pair.PairId).Single(notice => notice.ReasonKey == "unknown-pair-failure");
        await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, unknownPair.NoticeId!, NoticeChoices.RetryNow, CancellationToken.None);
        Require(store.SyncRetryAt(fx.Pair.PairId, string.Empty) is null,
            "unknown pair-level Retry never invents a root item");
        store.CloseNotice(unknownPair.NoticeId!, "resolved");

        // Show remaining is backed by the keyset page; offsets and invalid cursors are explained without changing holds.
        var shown = await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, summary[0].NoticeId!,
            NoticeChoices.ShowRemaining, CancellationToken.None);
        Require(shown.Contains(one.Path) && shown.Contains(two.Path) && shown.Contains("pending verification; not synced"),
            "Show remaining lists the held items from the page API");
        var offset = await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, summary[0].NoticeId!,
            NoticeChoices.ShowRemaining, CancellationToken.None, input: new(Offset: 50));
        Require(offset.Contains("next cursor (--name)") && store.CountHeldIntents(fx.Pair.PairId) == 2,
            "an offset is refused in favor of the keyset cursor without modifying holds");
        var invalid = await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, summary[0].NoticeId!,
            NoticeChoices.ShowRemaining, CancellationToken.None, input: new(Name: "not-a-cursor"));
        Require(invalid.Contains("cursor is invalid") && store.CountHeldIntents(fx.Pair.PairId) == 2,
            "an invalid cursor is explained without modifying holds");

        // A newly held intent updates the same combined summary instead of adding a second one.
        store.RequireRehash(fx.Pair.A.RootId, threeLive, "journal lost");
        store.RecordIntent(fx.Pair.PairId,
            new SyncAction("held-three", 1, ActionKind.CopyNew, Side.B, three.Path, null,
                threeLive with { Hash = threeHash }, null, false, "held-3"));
        var updatedReport = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        var updated = store.OpenNotices(fx.Pair.PairId).Single(notice => notice.ReasonKey == RehashProtection.Reason);
        Require(updated.NoticeId == summary[0].NoticeId && updated.Detail.Contains("3 changes are waiting for background verification") &&
            updated.Detail.Contains("3 files awaiting content verification"),
            "each reconciliation updates the same combined summary with the current counts");
        Require(updatedReport.PendingVerification == 3 && updatedReport.Held == updatedReport.PendingVerification &&
            updatedReport.Uncertain == 0,
            "the updated report counts every verification hold");

        // M4: already-held rows are not written again on a run that changes nothing.
        var writesBefore = store.IntentHoldWrites;
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(store.IntentHoldWrites == writesBefore,
            "a run skips hold-state writes for intents already held");

        // Release: only when BOTH counts are zero does the combined summary close; the released intents then sync.
        Require(store.CompleteRehash(fx.Pair.A.RootId, store.Live(fx.Pair.A.RootId, [one.Path]).Single(), oneHash) &&
            store.CompleteRehash(fx.Pair.A.RootId, store.Live(fx.Pair.A.RootId, [two.Path]).Single(), twoHash) &&
            store.CompleteRehash(fx.Pair.A.RootId, store.Live(fx.Pair.A.RootId, [three.Path]).Single(), threeHash),
            "content verification settles the flagged rows");
        var released = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(store.CountHeldIntents(fx.Pair.PairId) == 0 &&
            store.OpenNotices(fx.Pair.PairId).All(notice => notice.ReasonKey != RehashProtection.Reason),
            "releasing every hold and clearing every rehash closes the combined summary");
        Require(store.UnresolvedIntents(fx.Pair.PairId).Count == 0 && released.PendingVerification == 0 &&
            released.Uncertain == 0 && released.Failures.Count == 0,
            "released intents reconcile to Done rather than staying parked");
        Require(store.Baselines(fx.Pair.PairId).Any(row => row.A.Path == one.Path),
            "the synced baseline appears only after release");
        Console.WriteLine("PASS HeldIntentSummary");
    }

    // While protected, a pre-existing ordinary retry for the same pending effect is parked (evidence and
    // attempts retained) so the run reports a normal hold instead of uncertain debt. Release unparks the
    // deadline before returning the intent to uncertain, so the effect is retried.
    private static async Task HeldIntentRetryParksAsync()
    {
        using var store = SqliteStateStore.Open(
            Path.Combine(Path.GetTempPath(), "held-retry-" + Guid.NewGuid().ToString("N")),
            Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        store.MarkFirstSyncDone(fx.Pair.PairId);
        var source = fx.A.Seed("hold/retry.txt", "retry content");
        fx.B.Seed(source.Path, "retry content");
        var run = fx.Run();
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        var live = store.Live(fx.Pair.A.RootId, [source.Path]).Single();
        store.RequireRehash(fx.Pair.A.RootId, live, "journal lost");
        store.SaveSyncRetry(fx.Pair.PairId,
            new(source.Path, "sync", 6, "earlier failure", DateTimeOffset.UtcNow.AddMinutes(5)));
        store.RecordIntent(fx.Pair.PairId,
            new SyncAction("held-retry", 1, ActionKind.CopyNew, Side.B, source.Path, null,
                live with { Hash = source.Hash }, null, false, "held-retry-key"));

        var heldReport = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(store.IsIntentHeld(fx.Pair.PairId, "held-retry") && store.CountHeldIntents(fx.Pair.PairId) == 1,
            "the verification-protected intent is held rather than reported as uncertain debt");
        var parked = store.SyncRetryAt(fx.Pair.PairId, source.Path);
        Require(parked is { Kind: "sync", Attempts: 6, LastError: "earlier failure", RetryAfter: var parkedDeadline } &&
            parkedDeadline == DateTimeOffset.MaxValue,
            "the existing retry is parked at its deadline without changing evidence or attempts");
        Require(store.CountSyncRetries(fx.Pair.PairId) == 0,
            "the parked retry is no longer counted as active retry debt");
        Require(heldReport.PendingVerification == 1 && heldReport.Held == heldReport.PendingVerification &&
            heldReport.Uncertain == 0 && heldReport.Failures.Count == 0,
            "hold-only reporting stays normal even with prior ordinary retry debt");

        Require(store.CompleteRehash(fx.Pair.A.RootId, store.Live(fx.Pair.A.RootId, [source.Path]).Single(), source.Hash!),
            "verification settles the protected row");
        var released = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        // Release unparked the deadline before the held->uncertain transition, so this run reconciled the
        // intent, re-planned and applied it; successful evaluation then settled the ordinary retry debt.
        Require(store.SyncRetryAt(fx.Pair.PairId, source.Path) is null,
            "release retries the effect and successful evaluation clears the ordinary retry that was parked");
        Require(store.Baselines(fx.Pair.PairId).Any(row => row.A.Path == source.Path),
            "the baseline is written by the released effect, not by the hold");
        Require(released.PendingVerification == 0 && released.Held == 0 && released.Uncertain == 0 &&
            released.Failures.Count == 0,
            "the released run reports no held or unconfirmed work");
        Console.WriteLine("PASS HeldIntentRetryParks");
    }

    // M1 regression: the two outside writers (CommitResult, RecordIntent) overwrite state='held' without unparking.
    // The unpark must therefore key on the retry row alone. After an outside commit to Uncertain and a released
    // protection, the next run reconciles the effect (Done) instead of sleeping behind the old parked deadline.
    private static async Task HeldIntentUnparkAfterOutsideCommitAsync()
    {
        using var store = SqliteStateStore.Open(
            Path.Combine(Path.GetTempPath(), "held-unpark-" + Guid.NewGuid().ToString("N")),
            Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        store.MarkFirstSyncDone(fx.Pair.PairId);
        var source = fx.A.Seed("hold/outside.txt", "outside content");
        fx.B.Seed(source.Path, "outside content");
        var run = fx.Run();
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        var live = store.Live(fx.Pair.A.RootId, [source.Path]).Single();
        store.RequireRehash(fx.Pair.A.RootId, live, "journal lost");
        store.SaveSyncRetry(fx.Pair.PairId,
            new(source.Path, "sync", 4, "older failure", DateTimeOffset.UtcNow.AddMinutes(5)));
        var action = new SyncAction("held-outside", 1, ActionKind.CopyNew, Side.B, source.Path, null,
            live with { Hash = source.Hash }, null, false, "held-outside-key");
        store.RecordIntent(fx.Pair.PairId, action);
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(store.IsIntentHeld(fx.Pair.PairId, action.ActionId) &&
            store.SyncRetryAt(fx.Pair.PairId, source.Path) is { RetryAfter: var parked } && parked == DateTimeOffset.MaxValue,
            "setup: the intent is held and its retry parked");

        // Outside reconcile (quarantine/choice paths do this): commit Uncertain, overwriting 'held'.
        store.CommitResult(fx.Pair.PairId, store.BeginGeneration(fx.Pair.A.RootId, "outside"),
            action, new ActionResult(action.ActionId, Outcome.Uncertain, null, null, null, "outside observation"));
        Require(!store.IsIntentHeld(fx.Pair.PairId, action.ActionId) &&
            store.SyncRetryAt(fx.Pair.PairId, source.Path) is { RetryAfter: var stillParked } &&
            stillParked == DateTimeOffset.MaxValue,
            "an outside commit leaves the state uncertain but the retry row still parked (the M1 leak)");

        Require(store.CompleteRehash(fx.Pair.A.RootId, store.Live(fx.Pair.A.RootId, [source.Path]).Single(), source.Hash!),
            "verification settles the protected row");
        var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(store.SyncRetryAt(fx.Pair.PairId, source.Path) is null && report.Outcome == RunOutcomes.Complete &&
            report.Uncertain == 0 && report.Failures.Count == 0 && report.PendingVerification == 0,
            "the unpark keys on the retry row alone: the intent reconciles instead of sleeping and reporting Partial");
        Require(store.Baselines(fx.Pair.PairId).Any(row => row.A.Path == source.Path),
            "the reconciled copy commits its baseline rather than staying blocked");
        Console.WriteLine("PASS HeldIntentUnparkAfterOutsideCommit");
    }

    // M2: links and left-alone stubs never clear by themselves, so their intents retire under the EXISTING link or
    // stubs summary; no new notice is raised, nothing is applied, and no verification hold or retry debt is created.
    private static async Task HeldIntentLinkAndStubRetireAsync()
    {
        foreach (var kind in new[] { ItemFlags.Link, ItemFlags.Placeholder })
        {
            var label = kind == ItemFlags.Link ? "link" : "stub";
            using var store = SqliteStateStore.Open(
                Path.Combine(Path.GetTempPath(), "held-" + label + "-" + Guid.NewGuid().ToString("N")),
                Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            store.MarkFirstSyncDone(fx.Pair.PairId);
            var item = fx.A.Seed("protected/item.txt", "target content");
            fx.Provider.AddInventory(fx.Pair.A, new HeldFlaggedInventory(fx.A, item.Path, kind));
            var settings = kind == ItemFlags.Placeholder
                ? (Settings with { ReplaceOnlineOnlyStubs = false }) : Settings;
            var run = new SyncRun(store, fx.Executor, fx.Provider, settings, TimeProvider.System);
            // First run stores the link/stub row and raises its standing summary.
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            var summaryKey = kind == ItemFlags.Link
                ? LinkExclusions.Reason.Key : LinkExclusions.StubReason.Key;
            var before = store.OpenNotices(fx.Pair.PairId).Where(notice => notice.ReasonKey == summaryKey).ToArray();
            Require(before.Length == 1, label + ": the existing standing summary explains the protected path");

            store.SaveSyncRetry(fx.Pair.PairId,
                new(item.Path, "sync", 2, "earlier failure", DateTimeOffset.UtcNow.AddMinutes(5)));
            store.RecordIntent(fx.Pair.PairId,
                new SyncAction("protected", 1, ActionKind.CopyNew, Side.B, item.Path, null, item, null, false, "protected-key"));
            var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);

            Require(store.UnresolvedIntents(fx.Pair.PairId).Count == 0 && store.CountHeldIntents(fx.Pair.PairId) == 0,
                label + ": the protected intent retires instead of becoming a verification hold");
            Require(report.PendingVerification == 0 && report.Uncertain == 0 && report.Failures.Count == 0,
                label + ": retirement is neither background verification nor a retry reason");
            var after = store.OpenNotices(fx.Pair.PairId).ToArray();
            Require(after.Where(notice => notice.ReasonKey == summaryKey).Select(notice => notice.NoticeId)
                    .SequenceEqual(before.Select(notice => notice.NoticeId)) &&
                after.All(notice => notice.ReasonKey != RehashProtection.Reason &&
                    notice.ReasonKey is not ("held-intents" or "permanent-boundary")),
                label + ": retirement raises no new notice; the existing standing summary stays the only explanation");
            Require(store.SyncRetryAt(fx.Pair.PairId, item.Path) is null && fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0,
                label + ": retirement clears the ordinary retry and performs no endpoint effect");
        }
        Console.WriteLine("PASS HeldIntentLinkAndStubRetire");
    }

    // M2: quarantine, Defender and pair-wide decision holds stay pending and unsynced under their own notice. They
    // are outside the verification summary and must not become retry or scheduling debt or a Partial run. Each cause
    // is exercised independently; the baseline count proves the hold never syncs the item.
    private static async Task HeldIntentPendingStaysUnsyncedAsync()
    {
        foreach (var mode in new[] { "quarantine", "defender", "decision" })
        {
            using var store = SqliteStateStore.Open(
                Path.Combine(Path.GetTempPath(), "held-pending-" + mode + "-" + Guid.NewGuid().ToString("N")),
                Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            store.MarkFirstSyncDone(fx.Pair.PairId);
            var source = fx.A.Seed("hold/pending.txt", "pending content");
            fx.B.Seed(source.Path, "pending content");
            var run = fx.Run();
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            var baselinesBefore = store.Baselines(fx.Pair.PairId).Count;
            DefenderGrant? grant = null;
            switch (mode)
            {
                case "quarantine":
                    store.AddNotices([new Notice(fx.Pair.PairId, 30, NoticeKind.UserDecision, "hold",
                        "typed subtree hold", QuarantineFailure.NoticeKey,
                        Quarantine: new(Side.A, null, source, ScanOnly: true))]);
                    break;
                case "defender":
                    var fullPath = Path.GetFullPath(Path.Combine(fx.Pair.A.RootPath, "hold", "pending.txt"));
                    grant = new DefenderGrant("grant-1", "owner-object", null, "another-local-object",
                        fullPath, fullPath + ".stage");
                    store.StoreDefenderGrant(grant);
                    Require(store.DefenderGrantHoldPaths(fx.Pair.A.RootId).Contains(source.Path),
                        "setup: the recorded Defender exclusion holds the item");
                    break;
                default:
                    // A pair-wide decision hold covers every intent in the pair, exactly like HasRootDecisionHold.
                    store.SaveSyncRetry(fx.Pair.PairId,
                        new(string.Empty, "decision", 2, "pair-wide decision pending", DateTimeOffset.UtcNow.AddHours(1)));
                    break;
            }
            store.SaveSyncRetry(fx.Pair.PairId,
                new(source.Path, "sync", 3, "ordinary failure", DateTimeOffset.UtcNow.AddMinutes(5)));
            var action = new SyncAction("held-pending", 1, ActionKind.CopyNew, Side.B, source.Path, null, source, null, false, "pending-key");
            store.RecordIntent(fx.Pair.PairId, action);

            var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            Require(store.IntentById(fx.Pair.PairId, action.ActionId) is not null && store.CountHeldIntents(fx.Pair.PairId) == 0,
                mode + ": a pending-evidence hold stays unresolved but is not counted as background verification");
            Require(store.OpenNotices(fx.Pair.PairId).All(notice => notice.ReasonKey != RehashProtection.Reason) &&
                report.PendingVerification == 0,
                mode + ": pending evidence is outside the combined verification summary");
            Require(report.Uncertain == 0 && report.Failures.Count == 0 && report.Outcome == RunOutcomes.Complete &&
                report.Deferred == 0,
                mode + ": a pending hold is not a retry reason, schedules no AutoSync retry and does not make the run Partial");
            Require(store.Baselines(fx.Pair.PairId).Count == baselinesBefore,
                mode + ": a pending hold never writes a synced baseline");
            Require(store.SyncRetryAt(fx.Pair.PairId, source.Path) is { Kind: "sync", RetryAfter: var parked } &&
                parked == DateTimeOffset.MaxValue && store.CountSyncRetries(fx.Pair.PairId) == 0,
                mode + ": the item's ordinary retry is parked and the indefinite hold is not scheduling debt");
            Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0,
                mode + ": the pending intent performs no endpoint effect");

            // Clearing the hold releases ordinary planning; the retained intent reconciles to Done.
            switch (mode)
            {
                case "quarantine":
                    foreach (var notice in store.OpenNotices(fx.Pair.PairId)
                                 .Where(notice => notice.ReasonKey == QuarantineFailure.NoticeKey))
                        store.CloseNotice(notice.NoticeId!, "released");
                    break;
                case "defender":
                    Require(store.RetireDefenderGrant(grant!), "setup: the Defender grant retires");
                    // The held run publishes a quarantine notice for the conflicting grant. Mirror
                    // VerifyRemovedExclusionAsync: retirement also closes this grant's notices, not unrelated holds.
                    var grantNotices = store.OpenNotices(fx.Pair.PairId)
                        .Where(notice => notice.Quarantine?.ConflictingGrantId == grant!.GrantId).ToArray();
                    Require(grantNotices.Length > 0, "setup: the held run published the Defender grant notice");
                    foreach (var notice in grantNotices)
                        store.CloseNotice(notice.NoticeId!, "released");
                    Require(!store.DefenderGrantHoldPaths(fx.Pair.A.RootId).Contains(source.Path),
                        "setup: the retired Defender grant no longer holds the item");
                    break;
                default:
                    store.ClearSyncRetry(fx.Pair.PairId, string.Empty);
                    break;
            }
            var released = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            Require(store.IntentById(fx.Pair.PairId, action.ActionId) is null && released.Uncertain == 0 &&
                released.PendingVerification == 0 && released.Deferred == 0,
                mode + ": when the hold clears, the retained intent reconciles normally");
            Require(store.Baselines(fx.Pair.PairId).Any(row => row.A.Path == source.Path),
                mode + ": the baseline is written only by the released effect");
        }
        Console.WriteLine("PASS HeldIntentPendingStaysUnsynced");
    }

    // Owner-requested M1 regression through the actual quarantine choice dispatcher: a quarantine retry first
    // reconciles the recorded pending intent (Done here), which resolves it and clears the parked retry atomically.
    private static async Task HeldIntentQuarantineChoiceReconcilesAsync()
    {
        using var store = SqliteStateStore.Open(
            Path.Combine(Path.GetTempPath(), "held-quarantine-choice-" + Guid.NewGuid().ToString("N")),
            Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        store.MarkFirstSyncDone(fx.Pair.PairId);
        var source = fx.A.Seed("hold/choice.txt", "choice content");
        fx.B.Seed(source.Path, "choice content");
        var run = fx.Run();
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        var localItem = store.Live(fx.Pair.A.RootId, [source.Path]).Single();
        var targetItem = store.Live(fx.Pair.B.RootId, [source.Path]).Single();
        store.RequireRehash(fx.Pair.A.RootId, localItem, "journal lost");
        store.SaveSyncRetry(fx.Pair.PairId,
            new(source.Path, "sync", 5, "earlier failure", DateTimeOffset.UtcNow.AddMinutes(5)));
        var action = new SyncAction("held-choice", 1, ActionKind.CopyNew, Side.B, source.Path, null,
            localItem with { Hash = source.Hash }, targetItem, false, "choice-key");
        store.RecordIntent(fx.Pair.PairId, action);
        await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(store.IsIntentHeld(fx.Pair.PairId, action.ActionId) &&
            store.SyncRetryAt(fx.Pair.PairId, source.Path) is { RetryAfter: var parked } && parked == DateTimeOffset.MaxValue,
            "setup: the intent is held and its retry parked");

        // A reviewed quarantine notice names that still-pending effect; the choice reconciles it before any retry.
        store.AddNotices([new Notice(fx.Pair.PairId, 30, NoticeKind.UserDecision, source.Path,
            "review the quarantined item", QuarantineFailure.NoticeKey,
            Quarantine: new QuarantineNotice(Side.B, localItem, targetItem, PendingActionId: action.ActionId))]);
        var review = store.OpenNotices(fx.Pair.PairId).Single(notice => notice.Quarantine is not null);
        var result = await run.ResolveQuarantineAsync(fx.Pair.PairId, review.NoticeId!,
            NoticeChoices.QuarantineRetry, CancellationToken.None);
        Require(result.Outcome == Outcome.Done,
            "the quarantine choice reconciled the pending intent instead of resubmitting it");
        Require(store.IntentById(fx.Pair.PairId, action.ActionId) is null &&
            store.SyncRetryAt(fx.Pair.PairId, source.Path) is null && store.CountHeldIntents(fx.Pair.PairId) == 0,
            "the reconciled intent resolves and its parked retry is cleared without a later run");
        Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0,
            "reconciling an already-applied effect performs no endpoint write");
        Console.WriteLine("PASS HeldIntentQuarantineChoiceReconciles");
    }

    // M3 apply regression: the reviewed first-sync plan carries the preview's combined summary. Classification at
    // apply can resolve every hold, so the apply must write the post-classification summary (none), never refresh the
    // stored notice back to the preview's stale count after closing it at zero.
    private static async Task HeldIntentApplySummaryUsesPostClassificationAsync()
    {
        using var store = SqliteStateStore.Open(
            Path.Combine(Path.GetTempPath(), "held-apply-summary-" + Guid.NewGuid().ToString("N")),
            Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        var one = fx.A.Seed("hold/one.txt", "one");
        var two = fx.A.Seed("hold/two.txt", "two");
        // Seed stored rows before the preview so verification debt exists when its reconciliation classifies.
        store.WriteSnapshot(new(fx.Pair.A.RootId, Side.A, store.BeginGeneration(fx.Pair.A.RootId, "seed"), "",
            [one, two], []));
        store.RequireRehash(fx.Pair.A.RootId, one, "journal lost");
        store.RequireRehash(fx.Pair.A.RootId, two, "journal lost");
        var first = new SyncAction("held-one", 1, ActionKind.CopyNew, Side.B, one.Path, null, one, null, false, "held-1");
        var second = new SyncAction("held-two", 1, ActionKind.CopyNew, Side.B, two.Path, null, two, null, false, "held-2");
        store.RecordIntents(fx.Pair.PairId, [first, second]);
        var run = fx.Run();

        var plan = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(plan.Notices.Any(notice => notice.ReasonKey == RehashProtection.Reason &&
                notice.Detail.Contains("2 changes are waiting for background verification")),
            "the reviewed plan carries the preview's combined verification summary");

        // Both verification holds resolve between review and apply. The apply's own reconciliation must decide the
        // stored summary, not the plan's stale copy.
        var oneLive = store.Live(fx.Pair.A.RootId, [one.Path]).Single();
        var twoLive = store.Live(fx.Pair.A.RootId, [two.Path]).Single();
        Require(store.CompleteRehash(fx.Pair.A.RootId, oneLive, oneLive.Hash ?? one.Hash!) &&
            store.CompleteRehash(fx.Pair.A.RootId, twoLive, twoLive.Hash ?? two.Hash!),
            "setup: both verification holds settle between review and apply");
        // Model the completed outside copy with its actual destination state; Done must form a valid baseline.
        var copiedOne = fx.B.Seed(one.Path, "one");
        store.CommitResult(fx.Pair.PairId, store.BeginGeneration(fx.Pair.A.RootId, "outside"), first,
            new ActionResult(first.ActionId, Outcome.Done, null, copiedOne, null, null));
        var report = await run.ApplyAsync(fx.Pair.PairId, plan, new RecordingProgress(), CancellationToken.None);
        Require(store.CountHeldIntents(fx.Pair.PairId) == 0 && report.PendingVerification == 0,
            "apply resolved every verification hold");
        Require(store.OpenNotices(fx.Pair.PairId).All(notice => notice.ReasonKey != RehashProtection.Reason),
            "apply never reopens the combined summary from the reviewed plan's stale count");
        Console.WriteLine("PASS HeldIntentApplySummaryUsesPostClassification");
    }

    // N1: the root-decision exclusion must not hide a decision whose notice write failed. A root decision row
    // carrying PendingNotice is waiting for that notice, so it still counts as scheduling debt; once the notice is
    // written the same row is the indefinite pair-wide hold the exclusion is meant for.
    private static Task HeldIntentPendingNoticeCountsAsync()
    {
        using var store = SqliteStateStore.Open(
            Path.Combine(Path.GetTempPath(), "held-pending-notice-" + Guid.NewGuid().ToString("N")),
            Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        var attention = new Notice(fx.Pair.PairId, 40, NoticeKind.UserDecision, string.Empty,
            "deletion review pending", "deletion-review");
        store.SaveSyncRetry(fx.Pair.PairId,
            new SyncRetry(string.Empty, "decision", 0, "waiting for the user", DateTimeOffset.MinValue));
        Require(store.CountSyncRetries(fx.Pair.PairId) == 0,
            "the root decision hold without a pending notice is still not scheduling debt");
        store.SaveSyncRetry(fx.Pair.PairId, new SyncRetry(string.Empty, "decision", 0, "Notice: store unavailable",
            DateTimeOffset.MinValue, PendingNotice: attention));
        Require(store.CountSyncRetries(fx.Pair.PairId) == 1,
            "a root decision still waiting for its notice counts as scheduling debt");
        store.SaveSyncRetry(fx.Pair.PairId,
            new SyncRetry(string.Empty, "decision", 0, "", DateTimeOffset.MaxValue));
        Require(store.CountSyncRetries(fx.Pair.PairId) == 0,
            "once the notice is written the same row is the indefinite pair-wide hold, not debt");
        Console.WriteLine("PASS HeldIntentPendingNoticeCounts");
        return Task.CompletedTask;
    }

    // N2: retirement drops an intent without observing its effect, so a destructive one held by a link or stub stays
    // pending instead. This is the crash-interrupted case: a first-sync approved cloud delete of a rebuildable folder
    // was written and the apply crashed before committing, and the next reconcile must not retire it unobserved.
    private static async Task HeldIntentDestructiveLinkAndStubStayPendingAsync()
    {
        foreach (var kind in new[] { ItemFlags.Link, ItemFlags.Placeholder })
        {
            var label = kind == ItemFlags.Link ? "link" : "stub";
            using var store = SqliteStateStore.Open(
                Path.Combine(Path.GetTempPath(), "held-" + label + "-delete-" + Guid.NewGuid().ToString("N")),
                Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            store.MarkFirstSyncDone(fx.Pair.PairId);
            // A link protects a whole folder delete; only files count as placeholders, so the stub case deletes a file.
            var target = kind == ItemFlags.Link
                ? fx.A.Seed("protected/folder", string.Empty, ItemKind.Folder)
                : fx.A.Seed("protected/stub.txt", "stub content");
            if (kind == ItemFlags.Link) fx.A.Seed("protected/folder/child.txt", "child");
            fx.Provider.AddInventory(fx.Pair.A, new HeldFlaggedInventory(fx.A, target.Path, kind));
            var settings = kind == ItemFlags.Placeholder
                ? (Settings with { ReplaceOnlineOnlyStubs = false }) : Settings;
            var run = new SyncRun(store, fx.Executor, fx.Provider, settings, TimeProvider.System);
            // The first run stores the link/stub row and raises its standing summary.
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            var summaryKey = kind == ItemFlags.Link
                ? LinkExclusions.Reason.Key : LinkExclusions.StubReason.Key;
            Require(store.OpenNotices(fx.Pair.PairId).Count(notice => notice.ReasonKey == summaryKey) == 1,
                label + ": setup leaves the standing summary explaining the protected item");
            Require(store.CountDeferredPlanning(fx.Pair.PairId) == 0,
                label + ": full SyncRun creates no deferred rows for permanent holds");
            if (kind == ItemFlags.Link)
            {
                fx.B.Seed(target.Path, "changed counterpart");
                await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
                Require(store.CountDeferredPlanning(fx.Pair.PairId) == 0,
                    "changed counterpart under a permanent link still creates no deferred row");
            }
            var baselinesBefore = store.Baselines(fx.Pair.PairId).Count;

            store.SaveSyncRetry(fx.Pair.PairId,
                new(target.Path, "sync", 2, "earlier failure", DateTimeOffset.UtcNow.AddMinutes(5)));
            var action = new SyncAction("held-delete", 31, ActionKind.Delete, Side.B, target.Path, null, null,
                target, false, "held-delete-key") { RebuildableRemove = true, FirstSyncChoice = true };
            store.RecordIntent(fx.Pair.PairId, action);
            var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);

            Require(store.IntentById(fx.Pair.PairId, action.ActionId) is not null &&
                store.UnresolvedIntents(fx.Pair.PairId).Count == 1,
                label + ": an unverified destructive intent stays pending instead of retiring under the summary");
            Require(!store.IsIntentHeld(fx.Pair.PairId, action.ActionId) && store.CountHeldIntents(fx.Pair.PairId) == 0 &&
                report.PendingVerification == 0,
                label + ": the pending delete is outside the combined verification summary");
            Require(report.Uncertain == 0 && report.Failures.Count == 0 && report.Outcome == RunOutcomes.Complete &&
                report.Deferred == 0,
                label + ": a pending delete is not a failure, a Partial run or scheduling debt");
            Require(store.SyncRetryAt(fx.Pair.PairId, target.Path) is { Kind: "sync", RetryAfter: var parked } &&
                parked == DateTimeOffset.MaxValue && store.CountSyncRetries(fx.Pair.PairId) == 0,
                label + ": its ordinary retry is parked, so the pending delete schedules no retry loop");
            Require(store.Baselines(fx.Pair.PairId).Count == baselinesBefore &&
                fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0,
                label + ": the pending delete is never applied and writes no baseline");

            // The crash-interrupted intent survives repeated runs: only a verified reconciliation may resolve it.
            await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
            Require(store.IntentById(fx.Pair.PairId, action.ActionId) is not null &&
                store.UnresolvedIntents(fx.Pair.PairId).Count == 1 && store.CountSyncRetries(fx.Pair.PairId) == 0,
                label + ": the unverified delete stays pending on later runs too");
        }
        Console.WriteLine("PASS HeldIntentDestructiveLinkAndStubStayPending");
    }

    /// <summary>An inventory that reports one path with a chosen flag, as the real scanners do for links/stubs.</summary>
    private sealed class HeldFlaggedInventory(FakeEndpoint inner, string path, ItemFlags flags) : IInventorySource
    {
        public async Task<Snapshot> FullAsync(long gen, Func<string, bool> include, IProgress<string> p,
            CancellationToken ct)
        {
            var snapshot = await inner.FullAsync(gen, include, p, ct);
            return snapshot with
            {
                Items = snapshot.Items.Select(item => item.Path == path ? item with { Flags = flags } : item).ToArray()
            };
        }

        public Task<Delta> ChangesSinceAsync(string cursor, long gen, Func<string, bool> include, CancellationToken ct) =>
            inner.ChangesSinceAsync(cursor, gen, include, ct);

        public Task<IReadOnlyDictionary<string, string>> HashAsync(IReadOnlyList<string> paths, CancellationToken ct) =>
            inner.HashAsync(paths, ct);

        public Task<IReadOnlyDictionary<string, DateTimeOffset>> DeletionTimesAsync(IReadOnlyList<string> paths,
            CancellationToken ct) => inner.DeletionTimesAsync(paths, ct);
    }
}
