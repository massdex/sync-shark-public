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

    private static void Completion_SharedFailurePolicy()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var expected = new[] { 10, 20, 40, 80, 160, 300, 300 };
        for (var attempt = 0; attempt < expected.Length; attempt++)
        {
            var state = FailureRetryOwner.Failed(V2Settings.Default, attempt, now);
            Require(state.Attempts == attempt + 1 && state.RetryAfter == now.AddSeconds(expected[attempt]),
                "shared failure owner preserves the ten-second exponential policy capped at five minutes");
        }
        Require(FailureRetryOwner.Failed(V2Settings.Default, int.MaxValue, now) == new FailureRetryOwner.State(int.MaxValue, now.AddSeconds(300)),
            "long-lived failures saturate their durable count rather than overflowing into immediate retries");
        Require(FailureRetryOwner.Failed(V2Settings.Default, 2, now, countAttempt: false).Attempts == 2,
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
            var retry = FailureRetryOwner.SaveFailedRetry(V2Settings.Default, store, pairId, "item.txt", "locked", null, now);
            Require(retry.Attempts == 1, "first failure belongs to the entity's durable row");
            var pending = FailureRetryOwner.RetryNotice(pairId, "item.txt", "locked");
            typeof(SyncRun).GetMethod("SavePendingNoticeFailure", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(fx.Run(), [pending, new IOException("notice store temporarily unavailable")]);
            Require(store.SyncRetryAt(pairId, "item.txt") is { Attempts: 1, PendingNotice: { DeliveryRetry: { Attempts: 1 } } } delivery &&
                delivery.RetryAfter == retry.RetryAfter,
                "failed notice delivery advances only its nested attempt, preserving file deadline and count");
            var decision = new SyncRetry("", "decision", 7, "review required", DateTimeOffset.MaxValue);
            store.SaveSyncRetry(pairId, decision);
            FailureRetryOwner.RecordRunFailure(V2Settings.Default, store, pairId, new IOException("run bookkeeping unavailable"), now, isStateDamage: StorageFailureClassifier.IsStateDamage);
            FailureRetryOwner.RecordRunFailure(V2Settings.Default, store, pairId, new IOException("run bookkeeping unavailable"), now.AddMinutes(1), isStateDamage: StorageFailureClassifier.IsStateDamage);
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
            var retry = FailureRetryOwner.SaveFailedRetry(V2Settings.Default, store, pairId, "item.txt", "still locked", old, now);
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

    // Adopted gRPC reconnect/backoff (READING 8.5; AutoSync 3.10.1): one schedule for the listener,
    // prerequisite, request, transfer and recovery attempts. The legacy entry points above stay unchanged.
    private static void Completion_AdoptedBackoffSchedule()
    {
        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var settings = new FailureRetryOwner.BackoffSettings(1, 1.6, 0.2, 120);
        static bool Same(TimeSpan actual, double seconds) => Math.Abs(actual.TotalSeconds - seconds) < 1e-6;
        static bool At(DateTimeOffset deadline, DateTimeOffset origin, double seconds) =>
            Math.Abs((deadline - origin).TotalSeconds - seconds) < 1e-6;
        TimeSpan Wait(int prior, double sample, TimeSpan? providerWait = null) =>
            FailureRetryOwner.AdoptedWait(prior, providerWait, settings, () => sample);

        // First consecutive failure selects the exact initial delay; jitter starts at the second tier.
        Require(Same(Wait(0, 0), 1) && Same(Wait(0, 0.5), 1) && Same(Wait(0, 1), 1),
            "adopted backoff: first failure is exactly the one-second initial delay");
        // Second tier: nominal 1.6 seconds with one +/-20% draw.
        Require(Same(Wait(1, 0), 1.28) && Same(Wait(1, 0.5), 1.6) && Same(Wait(1, 1), 1.92),
            "adopted backoff: second tier draws 1.28/1.6/1.92 seconds");
        // The nominal cap is applied before the draw; the selected delay is never clipped to the cap.
        Require(Same(Wait(100, 0), 96) && Same(Wait(100, 0.5), 120) && Same(Wait(100, 1), 144),
            "adopted backoff: nominal cap yields 96/120/144 seconds without clipping the final delay");
        // One draw selects the retained delay for a later failed attempt.
        var draws = 0;
        var measured = FailureRetryOwner.AdoptedWait(1, null, settings, () => { draws++; return 0.5; });
        Require(draws == 1 && Same(measured, 1.6),
            "adopted backoff: one jitter draw selects the retained delay");
        // An applicable provider wait, including a valid zero, takes precedence and needs no draw.
        var providerDraws = 0;
        var floor = FailureRetryOwner.AdoptedWait(3, TimeSpan.FromSeconds(45), settings,
            () => { providerDraws++; return 0; });
        var zero = FailureRetryOwner.AdoptedWait(3, TimeSpan.Zero, settings,
            () => { providerDraws++; return 0; });
        Require(floor == TimeSpan.FromSeconds(45) && zero == TimeSpan.Zero && providerDraws == 0,
            "adopted backoff: provider wait precedence needs no draw");
        // Pair state counts one attempt, saturates its count and starts its deadline at completion.
        var first = FailureRetryOwner.AdoptedFailed(0, now, null, settings, () => 0.5);
        var second = FailureRetryOwner.AdoptedFailed(1, now, null, settings, () => 0.5);
        var floorPair = FailureRetryOwner.AdoptedFailed(100, now, TimeSpan.FromSeconds(30), settings, () => 0.5);
        var saturated = FailureRetryOwner.AdoptedFailed(int.MaxValue, now, null, settings, () => 0.5);
        Require(first.Attempts == 1 && At(first.RetryAfter, now, 1) &&
            second.Attempts == 2 && At(second.RetryAfter, now, 1.6) &&
            floorPair.Attempts == 101 && floorPair.RetryAfter == now.AddSeconds(30) &&
            saturated.Attempts == int.MaxValue && At(saturated.RetryAfter, now, 120),
            "adopted pair state counts once, saturates and starts at completion");
        // Operation-local and pair episodes are independent; a pair failure never seeds an operation episode.
        FailureRetryOwner.AdoptedFailed(7, now, null, settings, () => 0.5);
        Require(Same(Wait(0, 0.5), 1),
            "operation-local wait stays independent of the pair episode");
        // Legacy entry points keep their ten-second/300-second schedule for their existing consumers.
        Require(FailureRetryOwner.Failed(V2Settings.Default, 0, now) == new FailureRetryOwner.State(1, now.AddSeconds(10)) &&
            V2Settings.Default.RetryMaximumDelay == TimeSpan.FromMinutes(5),
            "legacy failure entry points retain their ten-second/300-second schedule");
        Console.WriteLine("PASS Completion_AdoptedBackoffSchedule");
    }

    // Listener reconnect state: a provider floor outlives a success, and only an accepted response
    // resets the generic stage. The session is owned once per account/source identity for this process;
    // the provider floors themselves are owned separately per account/route/source.
    private static void Completion_AdoptedListenerBoundaries()
    {
        var clock = new ResilienceChecks.Clock();
        var settings = new FailureRetryOwner.BackoffSettings(1, 1.6, 0.2, 120);
        static bool Same(TimeSpan actual, double seconds) => Math.Abs(actual.TotalSeconds - seconds) < 1e-6;
        var identity = "listener-" + Guid.NewGuid().ToString("N");
        var session = FailureRetryOwner.SessionFor(identity);
        Require(ReferenceEquals(session, FailureRetryOwner.SessionFor(identity)) &&
            !ReferenceEquals(session, FailureRetryOwner.SessionFor(identity + "-other")),
            "listener state is owned once per account/source identity");
        // First failure: exact initial delay; the listener installs no provider floor.
        Require(FailureRetryOwner.RemainingProviderConstraint((identity, "any-route", "any-source"), clock) is null &&
            Same(FailureRetryOwner.AdoptedListenerWait(session, null, settings, () => 0.5), 1) &&
            FailureRetryOwner.RemainingProviderConstraint((identity, "any-route", "any-source"), clock) is null,
            "listener: first transient failure selects the one-second delay without a floor");
        // A provider floor wins, is retained by its owner, and still gates a later reply without one.
        FailureRetryOwner.InstallProviderConstraint((identity, null, null), clock.GetTimestamp(),
            TimeSpan.FromSeconds(45), clock);
        Require(Same(FailureRetryOwner.AdoptedListenerWait(session, TimeSpan.FromSeconds(45), settings, () => 1), 45) &&
            FailureRetryOwner.RemainingProviderConstraint((identity, "any-route", "any-source"), clock) ==
                TimeSpan.FromSeconds(45) &&
            Same(FailureRetryOwner.AdoptedListenerWait(session,
                FailureRetryOwner.RemainingProviderConstraint((identity, "any-route", "any-source"), clock),
                settings, () => 0), 45),
            "listener: a provider floor takes precedence and is retained when a later reply supplies none");
        // Success resets only the generic stage; a live provider floor remains in force.
        FailureRetryOwner.AdoptedListenerSucceeded(session);
        Require(FailureRetryOwner.RemainingProviderConstraint((identity, "any-route", "any-source"), clock) ==
                TimeSpan.FromSeconds(45),
            "listener: an accepted response never clears a live provider floor");
        // Without a floor, success resets the generic stage to the initial delay.
        var reset = FailureRetryOwner.SessionFor("listener-" + Guid.NewGuid().ToString("N"));
        var first = FailureRetryOwner.AdoptedListenerWait(reset, null, settings, () => 0.5);
        var second = FailureRetryOwner.AdoptedListenerWait(reset, null, settings, () => 0.5);
        FailureRetryOwner.AdoptedListenerSucceeded(reset);
        var afterSuccess = FailureRetryOwner.AdoptedListenerWait(reset, null, settings, () => 0.5);
        Require(Same(first, 1) && Same(second, 1.6) && Same(afterSuccess, 1),
            "listener: only an accepted response resets the generic stage");
        // A provider-wait failure is not success: after the floor expires the stage continues, never restarts.
        var continuous = FailureRetryOwner.SessionFor("listener-" + Guid.NewGuid().ToString("N"));
        FailureRetryOwner.AdoptedListenerWait(continuous, null, settings, () => 0.5);
        FailureRetryOwner.AdoptedListenerWait(continuous, TimeSpan.FromSeconds(45), settings, () => 0.5);
        Require(FailureRetryOwner.AdoptedListenerWait(continuous, null, settings, () => 0.5) > TimeSpan.FromSeconds(1),
            "listener: a provider-wait failure continues the consecutive-failure stage rather than resetting it");
        AdoptedProviderFloorBoundaries(clock);
        Console.WriteLine("PASS Completion_AdoptedListenerBoundaries");
    }

    // Table rows B and C, plus one own-wait remainder: the shared floors are installed once and read
    // by the send/monitor owners, never retained a second time by the polling session.
    private static void AdoptedProviderFloorBoundaries(ResilienceChecks.Clock clock)
    {
        // Row B: any valid supplied wait on any route or source gates every route and source on that
        // account only.
        var account = "floors-" + Guid.NewGuid().ToString("N");
        FailureRetryOwner.InstallProviderConstraint((account, null, null), clock.GetTimestamp(),
            TimeSpan.FromSeconds(30), clock);
        Require(FailureRetryOwner.RemainingProviderConstraint((account, "files/download", "source-a"), clock) ==
                TimeSpan.FromSeconds(30) &&
            FailureRetryOwner.RemainingProviderConstraint((account, "files/list_folder/longpoll", "source-b"), clock) ==
                TimeSpan.FromSeconds(30) &&
            FailureRetryOwner.RemainingProviderConstraint((account + "-other", "files/download", "source-a"), clock) is null,
            "provider floor: an account-wide wait gates every route and source on that account only");
        // Row C: an accepted longpoll backoff matches only its own route and source.
        var longpoll = "longpoll-" + Guid.NewGuid().ToString("N");
        var longpollIdentity = (AccountId: longpoll, RouteName: "files/list_folder/longpoll", SourceIdentity: "source");
        FailureRetryOwner.InstallProviderConstraint(longpollIdentity, clock.GetTimestamp(),
            TimeSpan.FromSeconds(90), clock);
        Require(FailureRetryOwner.RemainingProviderConstraint(longpollIdentity, clock) == TimeSpan.FromSeconds(90) &&
            FailureRetryOwner.RemainingProviderConstraint((longpoll, "files/list_folder/longpoll", "other-source"), clock) is null &&
            FailureRetryOwner.RemainingProviderConstraint((longpoll, "files/download", "source"), clock) is null,
            "provider floor: a longpoll wait matches only its own route and source");
        // A shorter or zero observation never shortens a live floor; a longer one replaces it.
        clock.Advance(TimeSpan.FromSeconds(40));
        FailureRetryOwner.InstallProviderConstraint(longpollIdentity, clock.GetTimestamp(),
            TimeSpan.FromSeconds(5), clock);
        FailureRetryOwner.InstallProviderConstraint(longpollIdentity, clock.GetTimestamp(), TimeSpan.Zero, clock);
        Require(FailureRetryOwner.RemainingProviderConstraint(longpollIdentity, clock) == TimeSpan.FromSeconds(50),
            "provider floor: a shorter or zero observation never shortens a live floor");
        FailureRetryOwner.InstallProviderConstraint(longpollIdentity, clock.GetTimestamp(),
            TimeSpan.FromSeconds(60), clock);
        Require(FailureRetryOwner.RemainingProviderConstraint(longpollIdentity, clock) == TimeSpan.FromSeconds(60),
            "provider floor: a longer observation replaces the live floor");
        // The longest applicable remainder wins; an account-wide floor still gates other routes.
        FailureRetryOwner.InstallProviderConstraint((longpoll, null, null), clock.GetTimestamp(),
            TimeSpan.FromSeconds(120), clock);
        Require(FailureRetryOwner.RemainingProviderConstraint(longpollIdentity, clock) == TimeSpan.FromSeconds(120) &&
            FailureRetryOwner.RemainingProviderConstraint((longpoll, "files/download", "source"), clock) ==
                TimeSpan.FromSeconds(120),
            "provider floor: the longest applicable remainder wins across matching scopes");
        // Expired floors are dropped by the owner and never returned again.
        clock.Advance(TimeSpan.FromSeconds(121));
        Require(FailureRetryOwner.RemainingProviderConstraint(longpollIdentity, clock) is null &&
            FailureRetryOwner.RemainingProviderConstraint((longpoll, "files/download", "source"), clock) is null,
            "provider floor: expired floors are dropped and never read again");
        // Observation: the remainder of one supplied wait, never shorter than zero and never null.
        var observed = clock.GetTimestamp();
        Require(FailureRetryOwner.RemainingProviderObservation(null, observed, clock) is null &&
            FailureRetryOwner.RemainingProviderObservation(TimeSpan.FromSeconds(30), null, clock) ==
                TimeSpan.FromSeconds(30),
            "observation: a missing wait stays null and a null anchor returns the full duration");
        clock.Advance(TimeSpan.FromSeconds(10));
        Require(FailureRetryOwner.RemainingProviderObservation(TimeSpan.FromSeconds(30), observed, clock) ==
                TimeSpan.FromSeconds(20) &&
            FailureRetryOwner.RemainingProviderObservation(TimeSpan.Zero, observed, clock) == TimeSpan.Zero,
            "observation: elapsed time is subtracted and a supplied zero stays zero");
        clock.Advance(TimeSpan.FromSeconds(25));
        Require(FailureRetryOwner.RemainingProviderObservation(TimeSpan.FromSeconds(30), observed, clock) == TimeSpan.Zero,
            "observation: elapsed past the wait is zero, never null");
    }
}