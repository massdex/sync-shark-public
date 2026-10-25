namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    private static void Completion_DeliveryPreservesPayload()
    {
        foreach (var reason in new[] { "run-unavailable", "recovery-restore-pending" })
        {
            var root = Path.Combine(Path.GetTempPath(), "delivery-payload-" + Guid.NewGuid().ToString("N"));
            var clock = new CompletionClock();
            var payload = new FailureRetryOwner.State(9, clock.Now.AddHours(2));
            var expected = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload);
            string pairId;
            Notice notice;
            using (var sql = SqliteStateStore.Open(root, Settings, clock, LocalItems.CaptureOptionalHash))
            {
                var failing = new RecordingStore(sql);
                var fx = Build(suppliedStore: failing);
                pairId = fx.Pair.PairId;
                notice = new(pairId, 40, NoticeKind.Info, "payload.txt", "original payload", reason,
                    "payload-notice", FailureRetry: payload, CauseKey: "original cause");
                failing.SaveSyncRetry(pairId, new("payload.txt", "sync", 4, "item error", clock.Now.AddHours(1)));
                failing.FailNoticePath = "payload.txt";
                var run = new SyncRun(failing, fx.Executor, fx.Provider, Settings, clock);
                typeof(SyncRun).GetMethod("WriteNotices", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(run, [new[] { notice }]);
                var saved = sql.SyncRetryAt(pairId, "payload.txt")!;
                Require(saved.Attempts == 4 && saved.PendingNotice?.DeliveryRetry is { Attempts: 1 } &&
                    System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(saved.PendingNotice!.FailureRetry!.Value).SequenceEqual(expected),
                    reason + ": failed delivery leaves payload failure state byte-identical and file count unchanged");
            }
            using (var sql = SqliteStateStore.Open(root, Settings, clock, LocalItems.CaptureOptionalHash))
            {
                var saved = sql.SyncRetryAt(pairId, "payload.txt")!;
                Require(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(saved.PendingNotice!.FailureRetry!.Value).SequenceEqual(expected),
                    reason + ": original payload failure state survives cold reopen");
                clock.Now = saved.PendingNotice.DeliveryRetry!.Value.RetryAfter.AddTicks(1);
                var fx = Build(suppliedStore: sql);
                var run = new SyncRun(sql, fx.Executor, fx.Provider, Settings, clock);
                typeof(SyncRun).GetMethod("RetryNotices", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                    .Invoke(run, [pairId]);
                // D85 / RQ-032: one notice per problem; ordinary payloads use the canonical identity,
                // not an arbitrary supplied id. Recovery notices retain their explicit identity.
                var delivered = sql.NoticeById(pairId, NoticeTable.Id(notice));
                Require(delivered is { } published && published.Notice.FailureRetry is not null &&
                    published.Notice.DeliveryRetry is null,
                    reason + ": delivered canonical notice retains payload state and clears delivery debt");
                Require(sql.SyncRetryAt(pairId, "payload.txt") is { Attempts: 4, PendingNotice: null } &&
                    System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(delivered!.Value.Notice.FailureRetry!.Value).SequenceEqual(expected),
                    reason + ": successful delivery removes pending debt and preserves payload failure state byte-identically");
            }
        }
    }

    private static async Task Completion_UnknownDeleteReplansAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "unknown-delete-replan-" + Guid.NewGuid().ToString("N"));
        using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var fx = Build(suppliedStore: store);
        fx.A.Seed("present.txt", "keep both");
        fx.B.Seed("present.txt", "keep both");
        var run = fx.Run();
        var preview = await run.PreviewFirstSyncAsync(fx.Pair.PairId, new RecordingProgress(), default);
        await run.ApplyAsync(fx.Pair.PairId, preview, new RecordingProgress(), default, ReviewedHeld(preview));
        var target = (await fx.B.ObserveAsync("present.txt", default))!;
        var action = new SyncAction("unknown-delete", 6, ActionKind.Delete, Side.B, target.Path, null, null,
            target, false, store.Baselines(fx.Pair.PairId).Single().ObjectKey)
            { ProviderEffect = new("delete-submit", "unknown", DateTimeOffset.UnixEpoch) };
        store.RecordIntent(fx.Pair.PairId, action);
        store.SaveSyncRetry(fx.Pair.PairId, new(action.Path, "sync", 1, "unknown submission", DateTimeOffset.MinValue));
        var observed = await EffectResolver.ResolveAsync(action, fx.WriterB, fx.WriterA, default, isStateDamage: StorageFailureClassifier.IsStateDamage,
            historicalDeleteSource: await fx.A.ObserveAsync(action.Path, default));
        Require(observed.Outcome == Outcome.Skipped && observed.Reason?.Contains("not", StringComparison.OrdinalIgnoreCase) == true,
            "E8 expired unknown submission with same target produces NotApplied observation");
        // Keep the intent unresolved here: the real incremental run must consume the observation, mark its
        // path dirty and freshly plan it, even though neither inventory feed has any changes.
        var report = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
        Require(store.IntentById(fx.Pair.PairId, action.ActionId) is null && store.SyncRetryAt(fx.Pair.PairId, action.Path) is null &&
            report.Failed == 0 && fx.WriterA.Deletes == 0 && fx.WriterB.Deletes == 0 &&
            await fx.A.ObserveAsync(action.Path, default) is not null && await fx.B.ObserveAsync(action.Path, default) is not null,
            "E8 real reconciliation dirties unchanged-cursor path, fresh planning settles it, and surviving source prevents deletion");
    }

    private static async Task Completion_IgnoreColdReopenAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "ignore-reopen-" + Guid.NewGuid().ToString("N"));
        string pairId, noticeId;
        var due = DateTimeOffset.UtcNow.AddMinutes(1);
        using (var store = SqliteStateStore.Open(root, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            var fx = Build(suppliedStore: store);
            pairId = fx.Pair.PairId;
            store.SaveSyncRetry(pairId, new("item.txt", "sync", 2, "locked", due));
            store.AddNotices([FailureRetryOwner.RetryNotice(pairId, "item.txt", "locked", 2)]);
            noticeId = store.OpenNotices(pairId).Single().NoticeId!;
            await fx.Run().ApplyRetryNoticeChoiceAsync(pairId, noticeId, NoticeChoices.Ignore, default);
            Require(store.SyncRetryAt(pairId, "item.txt") is { Attempts: 2 } && store.Pair(pairId)!.Excluded.Count == 0,
                "real Ignore handler preserves retry debt and does not exclude the path");
            store.AddNotices([new(pairId, 30, NoticeKind.Info, "unreadable.txt", "locked", "unreadable")]);
            var unreadable = store.OpenNotices(pairId).Single(item => item.Path == "unreadable.txt");
            await fx.Run().ApplyRetryNoticeChoiceAsync(pairId, unreadable.NoticeId!, NoticeChoices.RetryNow, default);
            Require(store.SyncRetryAt(pairId, "unreadable.txt") is { Kind: "scan", ReadRoots.Count: 2 } exact &&
                exact.RetryAfter == DateTimeOffset.MinValue,
                "unreadable Retry records exact durable read work, not a generic planning-only retry");
            store.CloseNotice(unreadable.NoticeId!, "resolved");
        }
        using (var reopened = SqliteStateStore.Open(root, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            Require(reopened.SyncRetryAt(pairId, "unreadable.txt") is { Kind: "scan", ReadRoots.Count: 2 },
                "exact unreadable retry roots survive a cold reopen");
            Require(reopened.SyncRetryAt(pairId, "item.txt") is { Attempts: 2, LastError: "locked" } retry &&
                retry.RetryAfter == due && reopened.Pair(pairId)!.Excluded.Count == 0 &&
                reopened.NoticeById(pairId, noticeId) is { Closed: false } && reopened.OpenNotices(pairId).Count == 0 &&
                reopened.RetryProtected(pairId, "item.txt", new HashSet<string>(StringComparer.OrdinalIgnoreCase), false),
                "real-handler Ignore survives cold reopen: same due retry, no exclusion, suppressed actionable notice and protection");
            reopened.AddNotices([FailureRetryOwner.RetryNotice(pairId, "item.txt", "different cause", 3)]);
            Require(reopened.OpenNotices(pairId).Single().NoticeId == noticeId,
                "new cause resurfaces the same entity notice after cold reopen");
            reopened.ClearSyncRetry(pairId, "item.txt");
            Require(reopened.NoticeById(pairId, noticeId) is { Closed: true },
                "later success closes the previously suppressed notice");
        }
    }

    // RQ-032: a path the settle step keeps refusing shows one folded "not synced yet" line, not one per run,
    // and the line closes when the path settles. Item retries and specific observations keep their own notice.
    private static void DeferredRefusals_FoldedAndCleared()
    {
        var folder = FixtureFolder("deferred-refusal");
        try
        {
            using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            var pairId = fx.Pair.PairId;
            var id = FailureRetryOwner.RetryNoticeId(pairId, "waiting.txt");
            FailureRetryOwner.ReportDeferredRefusal(store, pairId, "waiting.txt", "waiting for Folder");
            FailureRetryOwner.ReportDeferredRefusal(store, pairId, "waiting.txt", "waiting for Folder");
            var notices = store.OpenNotices(pairId).Where(notice => notice.Path == "waiting.txt").ToArray();
            Require(notices.Length == 1 && notices[0].NoticeId == id && notices[0].ReasonKey == "sync-retry" &&
                notices[0].Detail.Contains("not synced yet", StringComparison.Ordinal) &&
                notices[0].Detail.Contains("waiting for Folder", StringComparison.Ordinal),
                "a repeatedly refused settle path keeps one folded not-synced-yet notice across runs");
            FailureRetryOwner.ClearDeferredRefusal(store, pairId, "waiting.txt");
            Require(store.NoticeById(pairId, id) is { Closed: true },
                "the folded refusal notice closes when the path settles");
            store.SaveSyncRetry(pairId, new("retry.txt", "sync", 1, "permission denied", DateTimeOffset.UtcNow.AddMinutes(1)));
            FailureRetryOwner.ReportDeferredRefusal(store, pairId, "retry.txt", "waiting for Other");
            Require(store.NoticeById(pairId, FailureRetryOwner.RetryNoticeId(pairId, "retry.txt")) is null,
                "an item retry owns its own explanation instead of a folded settle line");
            store.AddNotices([new(pairId, 30, NoticeKind.Info, "unreadable.txt", "cannot read", "unreadable")]);
            FailureRetryOwner.ReportDeferredRefusal(store, pairId, "unreadable.txt", "waiting for Other");
            var folded = store.OpenNotices(pairId).Where(notice => notice.Path == "unreadable.txt").ToArray();
            Require(folded.Length == 1 && folded[0].ReasonKey == "unreadable" &&
                folded[0].Detail.Contains("waiting for Other", StringComparison.Ordinal),
                "the generic settle line folds into the specific unreadable explanation");
        }
        finally { DeleteFixture(folder); }
    }

    // The acknowledgement wiring publishes one line per refused spelling across runs and clears it when the
    // same spelling is later reported resolved.
    private static void DeferredRefusals_WiringDedupes()
    {
        var folder = FixtureFolder("deferred-refusal-wiring");
        try
        {
            using var store = SqliteStateStore.Open(folder, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            var pairId = fx.Pair.PairId;
            var wiring = typeof(SyncRun).GetMethod("ReportDeferredRefusals",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            IReadOnlyList<SqliteStateStore.DeferredSettle> refused = [new("blocked.txt", Refused: true)];
            Func<string, bool> evaluated = _ => true;
            var unsettled = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            wiring.Invoke(fx.Run(), [pairId, refused, evaluated, unsettled]);
            wiring.Invoke(fx.Run(), [pairId, refused, evaluated, unsettled]);
            Require(store.OpenNotices(pairId).Count(notice => notice.Path == "blocked.txt") == 1,
                "two settled acknowledgement runs keep one folded refusal line");
            IReadOnlyList<SqliteStateStore.DeferredSettle> resolved = [new("blocked.txt", Refused: false)];
            wiring.Invoke(fx.Run(), [pairId, resolved, evaluated, unsettled]);
            Require(store.NoticeById(pairId, FailureRetryOwner.RetryNoticeId(pairId, "blocked.txt")) is { Closed: true },
                "the refusal line closes when the acknowledgement reports the spelling resolved");
        }
        finally { DeleteFixture(folder); }
    }

    private static void Completion_SharedFailurePolicy()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var expected = new[] { 10, 20, 40, 80, 160, 300, 300 };
        for (var attempt = 0; attempt < expected.Length; attempt++)
        {
            var state = FailureRetryOwner.Failed(attempt, now);
            Require(state.Attempts == attempt + 1 && state.RetryAfter == now.AddSeconds(expected[attempt]),
                "shared failure owner preserves the ten-second exponential policy capped at five minutes");
        }
        Require(FailureRetryOwner.Failed(int.MaxValue, now) == new FailureRetryOwner.State(int.MaxValue, now.AddSeconds(300)),
            "long-lived failures saturate their durable count rather than overflowing into immediate retries");
        Require(FailureRetryOwner.Failed(2, now, countAttempt: false).Attempts == 2,
            "restored evidence has one attempt owner, never a second counter increment");
        var root = Path.Combine(Path.GetTempPath(), "failure-owner-" + Guid.NewGuid().ToString("N"));
        string pairId;
        using (var store = SqliteStateStore.Open(root, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            var fx = Build(suppliedStore: store);
            pairId = fx.Pair.PairId;
            for (var index = 0; index < 300; index++)
            {
                var action = new SyncAction("failure-" + index, 0, ActionKind.CopyNew, Side.B,
                    "failures/" + index + ".txt", null, null, null, false, null);
                store.RecordIntent(pairId, action);
                store.CommitResult(pairId, 1, action, new(action.ActionId, Outcome.Failed, null, null, null, "cause-" + index));
            }
            Require(store.SyncRetryAt(pairId, "failures/299.txt") is { Attempts: 0, LastError: "cause-299" },
                "every result retains its own cause beyond bounded display samples without counting twice");
            var retry = FailureRetryOwner.SaveFailedRetry(store, pairId, "item.txt", "locked", null, now);
            Require(retry.Attempts == 1, "first failure belongs to the entity's durable row");
            var pending = FailureRetryOwner.RetryNotice(pairId, "item.txt", "locked");
            typeof(SyncRun).GetMethod("SavePendingNoticeFailure", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(fx.Run(), [pending, new IOException("notice store temporarily unavailable")]);
            Require(store.SyncRetryAt(pairId, "item.txt") is { Attempts: 1, PendingNotice: { DeliveryRetry: { Attempts: 1 } } } delivery &&
                delivery.RetryAfter == retry.RetryAfter,
                "failed notice delivery advances only its nested attempt, preserving file deadline and count");
            var decision = new SyncRetry("", "decision", 7, "review required", DateTimeOffset.MaxValue);
            store.SaveSyncRetry(pairId, decision);
            FailureRetryOwner.RecordRunFailure(store, pairId, new IOException("run bookkeeping unavailable"), now, isStateDamage: StorageFailureClassifier.IsStateDamage);
            FailureRetryOwner.RecordRunFailure(store, pairId, new IOException("run bookkeeping unavailable"), now.AddMinutes(1), isStateDamage: StorageFailureClassifier.IsStateDamage);
            Require(store.SyncRetryAt(pairId, "") == decision &&
                store.OpenNotices(pairId).Count(notice => notice.ReasonKey == "run-unavailable") == 1 &&
                FailureRetryOwner.RunFailureState(store, pairId) is { Attempts: 2 },
                "pair/run notice owns its counter without changing the root decision entity");
        }
        using (var store = SqliteStateStore.Open(root, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            Require(store.SyncRetryAt(pairId, "failures/299.txt")?.LastError == "cause-299",
                "per-item diagnosis survives cold reopen beyond the display sample");
            Require(FailureRetryOwner.RunFailureState(store, pairId) is { Attempts: 2 } &&
                store.SyncRetryAt(pairId, "") is { Kind: "decision", Attempts: 7 },
                "run notice debt and independent root decision survive cold reopen");
            store.CloseNotice(FailureRetryOwner.RunNoticeId(pairId), "resolved");
            Require(FailureRetryOwner.RunFailureState(store, pairId) is null,
                "successful run resolves its own failure entity");
            var old = store.SyncRetryAt(pairId, "item.txt");
            Require(old is { Attempts: 1, PendingNotice: { DeliveryRetry: { Attempts: 1 } } } && old.RetryAfter == now.AddSeconds(10),
                "file failure count/deadline and independent pending-notice delivery debt survive cold reopen");
            var retry = FailureRetryOwner.SaveFailedRetry(store, pairId, "item.txt", "still locked", old, now);
            Require(retry.Attempts == 2 && retry.RetryAfter == now.AddSeconds(20),
                "cold retry advances the same entity counter");
            store.ClearSyncRetry(pairId, ""); // End this fixture's independent decision before protection assertions.
            store.SaveSyncRetry(pairId, new("dormant.txt", "deletion-evidence", 0, "held", DateTimeOffset.MaxValue,
                ReprobeEvidence: true));
            var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var protection = store.OpenPlanningProtection(store.Pair(pairId)!))
            {
                var snapshot = protection.RetryLookup(pairId, active, restoredOnly: false);
                Require(!snapshot("dormant.txt", false) && !store.RetryProtected(pairId, "dormant.txt", active, false) &&
                    !store.SyncRetryExists(pairId, "dormant.txt") && !store.ReadSyncRetryPaths(pairId).Contains("dormant.txt"),
                    "one predicate excludes dormant evidence from snapshot, live, stream and direct protection reads");
                Require(snapshot("item.txt", false) && store.RetryProtected(pairId, "item.txt", active, false),
                    "snapshot and live protection retain ordinary failure debt");
            }
            store.ClearSyncRetry(pairId, "item.txt");
            Require(store.SyncRetryAt(pairId, "item.txt") is null, "successful entity retires its durable failure debt");
        }
    }
}
