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
                Settings, TimeProvider.System);
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
                NoticeChoices.Options(notices[0]).Select(option => option.Choice).SequenceEqual([NoticeChoices.Ignore]),
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

    // A temporary hold keeps the intent unresolved and reports it as Held/PendingVerification, never as an
    // unconfirmed or failed effect. The summary notice counts the holds, never holds a path itself, is backed
    // by the keyset page, updates its count, and closes when the last hold is released.
    private static async Task HeldIntentSummaryAsync()
    {
        using var store = SqliteStateStore.Open(
            Path.Combine(Path.GetTempPath(), "held-summary-" + Guid.NewGuid().ToString("N")),
            Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        store.MarkFirstSyncDone(fx.Pair.PairId);
        var one = fx.A.Seed("hold/one.txt", "one");
        var two = fx.A.Seed("hold/two.txt", "two");
        // A typed subtree hold protects the paths without an exclusion, so these intents are held, not settled.
        store.AddNotices([new Notice(fx.Pair.PairId, 30, NoticeKind.UserDecision, "hold", "subtree hold",
            QuarantineFailure.NoticeKey, Quarantine: new(Side.A, null, one, true))]);
        store.RecordIntents(fx.Pair.PairId,
        [
            new SyncAction("held-one", 1, ActionKind.CopyNew, Side.B, one.Path, null, one, null, false, "held-1"),
            new SyncAction("held-two", 1, ActionKind.CopyNew, Side.B, two.Path, null, two, null, false, "held-2"),
        ]);
        var run = fx.Run();

        var first = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(store.CountHeldIntents(fx.Pair.PairId) == 2 && store.CountUnresolvedIntents(fx.Pair.PairId) == 2 &&
            store.IsIntentHeld(fx.Pair.PairId, "held-one") && store.IsIntentHeld(fx.Pair.PairId, "held-two"),
            "temporary holds keep both intents unresolved and durably held");
        var held = store.OpenNotices(fx.Pair.PairId).Where(notice => notice.ReasonKey == "held-intents").ToArray();
        Require(held.Length == 1 && held[0].Path.Length == 0 && !PlanNotices.HasPathScope(held[0]),
            "one summary notice reports the holds and never holds a path");
        Require(held[0].Detail.Contains("2 changes are waiting"),
            "the summary counts the currently held intents");
        Require(NoticeChoices.IsRetryNotice(held[0]) &&
            NoticeChoices.Options(held[0]).Select(option => option.Choice).SequenceEqual([NoticeChoices.ShowRemaining]),
            "the summary offers Show remaining only");
        Require(first.PendingVerification == 2 && first.Held == first.PendingVerification && first.Uncertain == 0 &&
            first.Failures.Count == 0,
            "temporary holds are reported as held and pending, never as unconfirmed or failed effects");
        Require(fx.WriterA.Calls == 0 && fx.WriterB.Calls == 0 &&
            await fx.B.ObserveAsync(one.Path, default) is null && await fx.B.ObserveAsync(two.Path, default) is null,
            "held intents perform no endpoint effect");
        Require(store.Baselines(fx.Pair.PairId).Count == 0,
            "no synced baseline exists while the intents are still held");

        // Show remaining is the summary's only action and is backed by the keyset page; offsets and invalid
        // cursors are explained without changing any hold.
        var shown = await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, held[0].NoticeId!,
            NoticeChoices.ShowRemaining, CancellationToken.None);
        Require(shown.Contains(one.Path) && shown.Contains(two.Path) && shown.Contains("pending verification; not synced"),
            "Show remaining lists the held items from the page API");
        var offset = await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, held[0].NoticeId!,
            NoticeChoices.ShowRemaining, CancellationToken.None, input: new(Offset: 50));
        Require(offset.Contains("next cursor (--name)") && store.CountHeldIntents(fx.Pair.PairId) == 2,
            "an offset is refused in favor of the keyset cursor without modifying holds");
        var invalid = await run.ApplyRetryNoticeChoiceAsync(fx.Pair.PairId, held[0].NoticeId!,
            NoticeChoices.ShowRemaining, CancellationToken.None, input: new(Name: "not-a-cursor"));
        Require(invalid.Contains("cursor is invalid") && store.CountHeldIntents(fx.Pair.PairId) == 2,
            "an invalid cursor is explained without modifying holds");

        // A newly held intent updates the same summary instead of adding a second one.
        var three = fx.A.Seed("hold/three.txt", "three");
        store.RecordIntent(fx.Pair.PairId,
            new SyncAction("held-three", 1, ActionKind.CopyNew, Side.B, three.Path, null, three, null, false, "held-3"));
        var updatedReport = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        var updated = store.OpenNotices(fx.Pair.PairId).Single(notice => notice.ReasonKey == "held-intents");
        Require(updated.NoticeId == held[0].NoticeId && updated.Detail.Contains("3 changes are waiting"),
            "each reconciliation updates the same summary with the current count");
        Require(updatedReport.PendingVerification == 3 && updatedReport.Held == updatedReport.PendingVerification &&
            updatedReport.Uncertain == 0,
            "the updated report counts every temporary hold");

        // Release: the summary closes at zero and the released intents are processed normally.
        foreach (var notice in store.OpenNotices(fx.Pair.PairId)
                     .Where(notice => notice.ReasonKey == QuarantineFailure.NoticeKey))
            store.CloseNotice(notice.NoticeId!, "released");
        var released = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(store.CountHeldIntents(fx.Pair.PairId) == 0 &&
            store.OpenNotices(fx.Pair.PairId).All(notice => notice.ReasonKey != "held-intents"),
            "releasing every hold closes the summary notice");
        Require(released.PendingVerification == 0 && released.Uncertain == 0 && released.Failures.Count == 0,
            "a released run reports no remaining pending verification");
        Require((await fx.B.ObserveAsync(one.Path, default))?.Hash == one.Hash &&
            (await fx.B.ObserveAsync(two.Path, default))?.Hash == two.Hash &&
            (await fx.B.ObserveAsync(three.Path, default))?.Hash == three.Hash,
            "released intents are re-planned and applied normally");
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
            Settings, TimeProvider.System);
        var fx = Build(suppliedStore: store);
        store.MarkFirstSyncDone(fx.Pair.PairId);
        var source = fx.A.Seed("hold/retry.txt", "retry content");
        store.AddNotices([new Notice(fx.Pair.PairId, 30, NoticeKind.UserDecision, "hold", "subtree hold",
            QuarantineFailure.NoticeKey, Quarantine: new(Side.A, null, source, true))]);
        store.SaveSyncRetry(fx.Pair.PairId,
            new(source.Path, "sync", 6, "earlier failure", DateTimeOffset.UtcNow.AddMinutes(5)));
        store.RecordIntent(fx.Pair.PairId,
            new SyncAction("held-retry", 1, ActionKind.CopyNew, Side.B, source.Path, null, source, null, false, "held-retry-key"));
        var run = fx.Run();

        var heldReport = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        Require(store.IsIntentHeld(fx.Pair.PairId, "held-retry") && store.CountHeldIntents(fx.Pair.PairId) == 1,
            "the protected intent is held rather than reported as uncertain debt");
        var parked = store.SyncRetryAt(fx.Pair.PairId, source.Path);
        Require(parked is { Kind: "sync", Attempts: 6, LastError: "earlier failure", RetryAfter: var parkedDeadline } &&
            parkedDeadline == DateTimeOffset.MaxValue,
            "the existing retry is parked at its deadline without changing evidence or attempts");
        Require(store.CountSyncRetries(fx.Pair.PairId) == 0,
            "the parked retry is no longer counted as active retry debt");
        Require(heldReport.PendingVerification == 1 && heldReport.Held == heldReport.PendingVerification &&
            heldReport.Uncertain == 0 && heldReport.Failures.Count == 0,
            "hold-only reporting stays normal even with prior ordinary retry debt");
        Require(store.Baselines(fx.Pair.PairId).Count == 0,
            "no synced baseline exists before release");

        foreach (var notice in store.OpenNotices(fx.Pair.PairId)
                     .Where(notice => notice.ReasonKey == QuarantineFailure.NoticeKey))
            store.CloseNotice(notice.NoticeId!, "released");
        var released = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), CancellationToken.None);
        // Release unparked the deadline before the held->uncertain transition, so this run reconciled the
        // intent, re-planned and applied it; successful evaluation then settled the ordinary retry debt.
        Require(store.SyncRetryAt(fx.Pair.PairId, source.Path) is null,
            "release retries the effect and successful evaluation clears the ordinary retry that was parked");
        Require((await fx.B.ObserveAsync(source.Path, default))?.Hash == source.Hash,
            "the released intent is retried and its effect is applied");
        Require(store.Baselines(fx.Pair.PairId).Any(row => row.A.Path == source.Path),
            "the baseline is written by the released effect, not by the hold");
        Require(released.PendingVerification == 0 && released.Held == 0 && released.Uncertain == 0 &&
            released.Failures.Count == 0,
            "the released run reports no held or unconfirmed work");
        Console.WriteLine("PASS HeldIntentRetryParks");
    }
}
