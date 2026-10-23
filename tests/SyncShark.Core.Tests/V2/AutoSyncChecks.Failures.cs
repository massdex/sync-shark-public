namespace Massdex.SyncShark.V2;

// Failure-path checks from Astra's review of 68e8bde: foreign cancellations, access faults, dying watches, the real
// SyncRun gate, and losing waits that finish cancelling late.
internal static partial class AutoSyncChecks
{
    private static async Task FailureChecksAsync()
    {
        await LoopExceptionAndNoticeRecoveryAsync();
        await ImmediateAccessNoticeAsync();
        await ImmediateDiskFullReportAsync();
        await OutageCauseChangesStayIgnoredAsync();
        await ForeignCancellation_RetriesAsync();
        await AccessFault_RetriesAtSafetyPollAsync();
        await DyingWatches_BoundedRebuildsAsync();
        await SlowCancelWatches_NeverOverlapAsync();
        await RealRunGate_PausedQueuedNeverScansAsync();
        await ThrowingWatch_IsDeadWatchAsync();
        await ZeroSafetyPoll_UsesMinimumAsync();
    }

    internal static async Task LoopExceptionAndNoticeRecoveryAsync()
    {
        await using var rig = await Rig.StartedAsync();
        rig.ThrowEligibility = true;
        rig.Auto.Kick("p1");
        await Until(() => !rig.ThrowEligibility && rig.Clock.ActiveTimers == 1, "unexpected loop fault backs off");
        rig.Clock.Advance(Settings.MinSafetyPoll);
        await rig.ParkedAsync(AutoStatus.Waiting);
        rig.Clock.Advance(Settings.ChangeQuietWindow);
        await rig.ParkedAsync(AutoStatus.Idle);
        Require(rig.Run.Runs("p1") == 2, "pair loop survives an unexpected exception without a Kick");
        for (var i = 0; i < 4; i++) rig.Run.Then(new IOException("test provider unavailable"));
        rig.WatchA.Signal();
        await rig.ParkedAsync(AutoStatus.Waiting);
        rig.Clock.Advance(Settings.ChangeQuietWindow);
        await Until(() => rig.Run.Runs("p1") == 3 && rig.State("p1").RetryAt > rig.Clock.GetUtcNow() && rig.Clock.ActiveTimers == 1, "first failed run waits");
        var firstFailureNotices = rig._store.OpenNotices("p1");
        Console.WriteLine("TRANSIENT NOTICE MEASUREMENT count=" + firstFailureNotices.Count + "; " +
            string.Join(" | ", firstFailureNotices.Select(item => $"reason={item.ReasonKey}; attempts={item.FailureRetry?.Attempts}; detail={item.Detail}")));
        // RQ-032/shared failure-owner decision: the first failure is explained, never silently hidden until three runs.
        // Measured runtime: one canonical run-unavailable notice, Attempts=1, with retry guidance.
        Require(firstFailureNotices.Count == 1 && firstFailureNotices[0].NoticeId == FailureRetryOwner.RunNoticeId("p1") &&
            firstFailureNotices[0].FailureRetry is { Attempts: 1 } &&
            firstFailureNotices[0].Detail.Contains("test provider unavailable", StringComparison.Ordinal) &&
            NoticeOptions.Options(firstFailureNotices[0]).Any(option => option.Choice == NoticeChoices.RetryNow) &&
            NoticeOptions.Options(firstFailureNotices[0]).Any(option => option.Choice == NoticeChoices.Ignore),
            "first transient failure immediately gives one durable actionable notice");
        for (var i = 1; i < 4; i++)
        {
            var expected = 3 + i;
            rig.Clock.Advance(rig.State("p1").RetryAt!.Value - rig.Clock.GetUtcNow());
            await Until(() => rig.Run.Runs("p1") == expected && rig.State("p1").RetryAt > rig.Clock.GetUtcNow() && rig.Clock.ActiveTimers == 1, "failed run retries");
        }
        var notices = rig._store.OpenNotices("p1");
        Require(notices.Count == 1 && NoticeOptions.Options(notices[0]).Select(o => o.Choice).SequenceEqual(new[] { NoticeChoices.RetryNow, NoticeChoices.Ignore }),
            "repeated failures dedupe with Retry and Ignore, never root exclusion");
        rig.Clock.Advance(SafetyPoll);
        await rig.ParkedAsync(AutoStatus.Idle);
        Require(rig._store.OpenNotices("p1").Count == 0, "success automatically clears the outage notice");
        Pass("LoopExceptionAndNoticeRecovery");
    }

    internal static async Task ImmediateAccessNoticeAsync()
    {
        await using var rig = new Rig(["p1"]);
        rig.Run.Then(new EndpointAccessException("account authorization was revoked"));
        await rig.StartAsync();
        await rig.ParkedAsync(AutoStatus.Held);
        var notice = rig._store.OpenNotices("p1").Single();
        Console.WriteLine($"AUTH NOTICE MEASUREMENT reason={notice.ReasonKey}; detail={notice.Detail}");
        // Canonical pair/run owner decision: one run-unavailable notice, never a second autosync-failed notice.
        // Measured integrated runtime: run-unavailable with the explicit Reconnect instruction.
        Require(notice.ReasonKey == "run-unavailable" && notice.NoticeId == FailureRetryOwner.RunNoticeId("p1") &&
            notice.Detail.Contains("Reconnect", StringComparison.Ordinal) && notice.FailureRetry is { Attempts: 1 },
            "first auth failure immediately explains reconnect through one canonical durable run notice");
        Pass("ImmediateAccessNotice");
        foreach (var error in new Exception[] { new EndpointAccessException("completion access lost"), new StateDamagedException("completion store damaged") })
        {
            await using var completion = new Rig(["p1"]);
            completion.Run.Then(FakeSyncRun.Report("p1", RunOutcomes.Partial, 0) with { Done = 1, Uploaded = 1,
                CompletionFailed = true, HaltedBy = error });
            await completion.StartAsync();
            if (error is StateDamagedException)
            {
                // Measured fatal parking: Held, state_damaged, zero timers, no RetryAt, one run.
                // The fatal-State decision forbids the finite safety poll required by ParkedAsync.
                await Until(() => completion.Status("p1") == AutoStatus.Held &&
                    completion.State("p1").LastReport?.Outcome == RunOutcomes.StateDamaged &&
                    completion.Clock.ActiveTimers == 0, "fatal state parks indefinitely without a polling timer");
                completion.Clock.Advance(SafetyPoll * 2);
                completion.Auto.Kick("p1");
                Require(completion.State("p1").RetryAt is null && completion.Status("p1") == AutoStatus.Held &&
                    completion.Clock.ActiveTimers == 0 && completion.Run.Runs("p1") == 1,
                    "fatal hold has no scheduled retry or second run after time advances and Kick");
            }
            else await completion.ParkedAsync(AutoStatus.Held);
            if (error is StateDamagedException)
            {
                // Recorded fatal-State decision: no writes to damaged persistence; present the retained report.
                var retained = completion.State("p1").LastReport!;
                var projection = SyncRunOutcomeProjection.Classify(new SyncRunOutcomeInput { Outcome = retained.Outcome });
                Require(completion._store.OpenNotices("p1").Count == 0 &&
                    retained.Outcome == RunOutcomes.StateDamaged && retained.Done == 1 && retained.Uploaded == 1 &&
                    ReferenceEquals(retained.HaltedBy, error) &&
                    projection.NotificationDetail.Contains("backup", StringComparison.OrdinalIgnoreCase),
                    "fatal state preserves counted in-memory outcome and recovery guidance without a damaged-store notice");
                var forbidden = System.Reflection.DispatchProxy.Create<IStateStore, ForbiddenFailureStore>();
                var state = FailureRetryOwner.RecordRunFailure(forbidden, "p1", error, completion.Clock.GetUtcNow(), isStateDamage: StorageFailureClassifier.IsStateDamage);
                Require(((ForbiddenFailureStore)(object)forbidden).Calls == 0 && state.RetryAfter == DateTimeOffset.MaxValue,
                    "fatal failure owner performs zero store calls including notice writes");
            }
            else Require(completion._store.OpenNotices("p1").Single().Detail.Contains("Reconnect", StringComparison.Ordinal),
                "counted endpoint access failure holds pair with immediate reconnect guidance");
        }
    }

    public class ForbiddenFailureStore : System.Reflection.DispatchProxy
    {
        public int Calls { get; private set; }
        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            Calls++;
            throw new InvalidOperationException("Fatal failure attempted store access: " + targetMethod?.Name);
        }
    }

    internal static async Task CountedPauseDoesNotHoldAsync(RunReport report)
    {
        await using var rig = new Rig(["p1"]);
        rig.Run.Then(report);
        await rig.StartAsync();
        await rig.ParkedAsync(AutoStatus.Idle);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var before = rig.Run.Runs("p1");
            rig.Run.Then(report);
            rig.WatchA.Signal();
            await rig.ParkedAsync(AutoStatus.Waiting);
            rig.Clock.Advance(Settings.ChangeQuietWindow);
            await Until(() => rig.Run.Runs("p1") > before, "watch signal returns another paused report");
            await rig.ParkedAsync(AutoStatus.Idle);
        }
        Require(rig._store.OpenNotices("p1").Count == 0 && rig.State("p1").RetryAt is null,
            "three counted pauses neither hold AutoSync nor accumulate failure notices or retry backoff");
    }

    internal static async Task ImmediateDiskFullReportAsync()
    {
        await using var rig = new Rig(["p1"]);
        rig.Run.Then(FakeSyncRun.Report("p1", RunOutcomes.Failed, 0,
            new ActionResult("effect", Outcome.Failed, null, null, null, "disk full")
                { FailureHResult = unchecked((int)0x80070070) }));
        await rig.StartAsync();
        await rig.ParkedAsync(AutoStatus.Waiting);
        var diskNotice = rig._store.OpenNotices("p1").Single();
        Console.WriteLine($"DISK NOTICE MEASUREMENT reason={diskNotice.ReasonKey}; cause={diskNotice.CauseKey}; attempts={diskNotice.FailureRetry?.Attempts}; detail={diskNotice.Detail}");
        // One canonical run owner retains the actual cause rather than a second host classification label.
        Require(diskNotice.NoticeId == FailureRetryOwner.RunNoticeId("p1") && diskNotice.CauseKey == "disk full" &&
            diskNotice.FailureRetry is { Attempts: 1 } && diskNotice.Detail.Contains("available storage", StringComparison.Ordinal) &&
            NoticeOptions.Options(diskNotice).Any(option => option.Choice == NoticeChoices.RetryNow) &&
            NoticeOptions.Options(diskNotice).Any(option => option.Choice == NoticeChoices.Ignore),
            "disk failure immediately retains its exact cause with storage guidance and actionable durable retry");
        Pass("ImmediateDiskFullReport");
    }

    internal static async Task OutageCauseChangesStayIgnoredAsync()
    {
        await using var rig = await Rig.StartedAsync();
        async Task Fail(Exception error)
        {
            var before = rig.Run.Runs("p1");
            rig.Run.Then(error);
            if (rig.State("p1").RetryAt is { } retryAt)
            {
                // Waiting already describes the previous failure's backoff. A Kick followed by
                // Parked(Waiting) can observe that OLD wait before the Kick is consumed, then
                // advance the clock before the new debounce timer exists. Exercise automatic
                // retries instead: the previous Fail has joined the parked retry timer.
                rig.Clock.Advance(retryAt - rig.Clock.GetUtcNow());
            }
            else
            {
                rig.WatchA.Signal();
                await rig.ParkedAsync(AutoStatus.Waiting);
                rig.Clock.Advance(Settings.ChangeQuietWindow);
            }
            await Until(() => rig.Run.Runs("p1") == before + 1 && rig.State("p1").RetryAt > rig.Clock.GetUtcNow() &&
                rig.Clock.ActiveTimers == 1, "failure is parked");
        }
        for (var i = 0; i < 3; i++) await Fail(new HttpRequestException("network unavailable"));
        var notice = rig._store.OpenNotices("p1").Single();
        rig._store.CloseNotice(notice.NoticeId!, NoticeChoices.Ignore);
        for (var i = 0; i < 4; i++)
        {
            await Fail(i % 2 == 0 ? new IOException("socket unavailable") : new HttpRequestException("network unavailable"));
            Console.WriteLine("NOTICE-MEASURE outage-cause iteration=" + i + " original-id=" + notice.NoticeId +
                " visible=" + System.Text.Json.JsonSerializer.Serialize(rig._store.OpenNotices("p1").Select(n => new { n.NoticeId, n.ReasonKey, n.CauseKey })));
            // E9: round16 measured a changed cause visible on the SAME notice identity.
            var changed = rig._store.OpenNotices("p1");
            Require(changed.Count == 1 && changed[0].NoticeId == notice.NoticeId &&
                changed[0].CauseKey == (i % 2 == 0 ? "socket unavailable" : "network unavailable"),
                "a new cause resurfaces exactly the existing outage notice without duplication");
            rig._store.CloseNotice(notice.NoticeId!, NoticeChoices.Ignore);
            await Fail(i % 2 == 0 ? new IOException("socket unavailable") : new HttpRequestException("network unavailable"));
            Require(rig._store.OpenNotices("p1").Count == 0 &&
                rig._store.NoticeById("p1", notice.NoticeId!) is { Closed: false },
                "the same ignored cause stays hidden but actionable while automatic retries continue");
        }
        var beforeRecovery = rig.Run.Runs("p1");
        rig.Run.Then(FakeSyncRun.Report("p1", RunOutcomes.Failed, 0,
            new ActionResult("item", Outcome.Failed, null, null, null, "item permission denied")) with { ProviderReached = true });
        rig.Clock.Advance(rig.State("p1").RetryAt!.Value - rig.Clock.GetUtcNow());
        await Until(() => rig.Run.Runs("p1") == beforeRecovery + 1 && rig.State("p1").RetryAt > rig.Clock.GetUtcNow() &&
            rig.Clock.ActiveTimers == 1, "provider recovery with failed items is parked");
        for (var i = 0; i < 3; i++) await Fail(new HttpRequestException("second outage"));
        Require(rig._store.OpenNotices("p1").Count == 1, "success resets Ignore for the next outage");
        Pass("OutageCauseChangesStayIgnored");
    }

    private static async Task ThrowingWatch_IsDeadWatchAsync()
    {
        await using var rig = await Rig.StartedAsync();
        foreach (var (thrower, other, error) in new (string, string, Exception)[]
                 {
                     ("a-p1", "b-p1", new OperationCanceledException("foreign cancellation thrown directly")),
                     ("b-p1", "a-p1", new IOException("watch thrown directly"))
                 })
        {
            var runs = rig.Run.Runs("p1");
            var created = rig.Watches.Created;
            var waitingFirst = rig.Watches.Latest(other);
            rig.Watches.Latest(thrower).ThrowNext = error;
            waitingFirst.Signal();
            await rig.ParkedAsync(AutoStatus.Waiting);
            rig.Clock.Advance(Settings.ChangeQuietWindow);
            await rig.ParkedAsync(AutoStatus.Idle);
            Require(rig.Watches.Created == created + 2 && rig.Run.Runs("p1") == runs + 1,
                "a watch throwing directly (" + thrower + ") is a dead watch: rebuilt with a catch-up run");
            Require(!waitingFirst.Parked, "the other watch's started wait was cancelled and joined");
        }
        Pass("ThrowingWatch_IsDeadWatch");
    }

    private static async Task ZeroSafetyPoll_UsesMinimumAsync()
    {
        await using var rig = new Rig(["p1"]) { PollInterval = TimeSpan.Zero };
        await rig.StartAsync();
        await rig.ParkedAsync(AutoStatus.Idle);
        rig.Clock.Advance(Settings.MinSafetyPoll - TimeSpan.FromSeconds(1));
        await rig.SettledAsync();
        Require(rig.Run.Runs("p1") == 1 && rig.Watches.Created == 2, "a zero interval does not spin");
        rig.Clock.Advance(TimeSpan.FromSeconds(1));
        await rig.ParkedAsync(AutoStatus.Waiting);
        rig.Clock.Advance(Settings.ChangeQuietWindow);
        await rig.ParkedAsync(AutoStatus.Idle);
        Require(rig.Run.Runs("p1") == 2, "the safety poll runs at MinSafetyPoll");
        Pass("ZeroSafetyPoll_UsesMinimum");
    }

    private static async Task ForeignCancellation_RetriesAsync()
    {
        await using (var rig = new Rig(["p1"]))
        {
            rig.Run.Then(new TaskCanceledException("HTTP timeout inside the run"));
            await rig.StartAsync();
            await rig.ParkedAsync(AutoStatus.Waiting);
            Require(rig.State("p1").RetryAt is not null, "a cancellation the loop did not ask for is retried");
            rig.Clock.Advance(Settings.AutoRetryDelay);
            await rig.ParkedAsync(AutoStatus.Idle);
            Require(rig.Run.Runs("p1") == 2, "the loop survives it and the retry runs");
            rig.WatchA.CancelUnasked();
            await rig.ParkedAsync(AutoStatus.Waiting);
            rig.Clock.Advance(Settings.ChangeQuietWindow);
            await rig.ParkedAsync(AutoStatus.Idle);
            Require(rig.Run.Runs("p1") == 3 && rig.Watches.Created == 4,
                "a watch wait cancelled by its provider is a dead watch: rebuilt with a catch-up run");
        }
        await using (var arming = new Rig(["p1"]))
        {
            arming.Watches.WatchError = new OperationCanceledException("provider timeout while arming");
            arming.Auto.Start();
            await Until(() => arming.Run.Runs("p1") == 1 && arming.Clock.ActiveTimers == 1, "a cancelled arm degrades to polling instead of ending sync");
            Require(arming._store.OpenNotices("p1").Count(n => n.ReasonKey == "autosync-watch-unavailable") == 1, "one actionable watch notice");
        }
        Pass("ForeignCancellation_Retries");
    }

    private static async Task AccessFault_RetriesAtSafetyPollAsync()
    {
        await using var rig = await Rig.StartedAsync();
        rig.WatchA.Fail(new EndpointAccessException("revoked", new HttpRequestException("401")));
        // Superseded by the core rule: watch-only access loss cannot prohibit ordinary run preparation.
        await Until(() => rig.Run.Runs("p1") == 2 && rig.Clock.ActiveTimers == 1, "watch access failure permits polling");
        Require(rig._store.OpenNotices("p1").Count(n => n.ReasonKey == "autosync-watch-unavailable") == 1,
            "watch access failure has one notice");
        rig.Clock.Advance(Settings.AutoRetryDelay);
        await rig.ParkedAsync(AutoStatus.Waiting);
        rig.Clock.Advance(Settings.ChangeQuietWindow);
        await rig.ParkedAsync(AutoStatus.Idle);
        Require(rig.Run.Runs("p1") == 3, "watch recovery gives a catch-up run");
        Require(!rig._store.OpenNotices("p1").Any(n => n.ReasonKey == "autosync-watch-unavailable"),
            "recovered watch closes its own notice");
        Pass("AccessFault_RetriesAtSafetyPoll");
    }

    private static async Task DyingWatches_BoundedRebuildsAsync()
    {
        await using var rig = new Rig(["p1"]);
        rig.Watches.WaitError = new IOException("watcher dies at once");
        rig.Auto.Start();
        // Superseded: notification failure degrades to polling, never sync admission refusal.
        await Until(() => rig.Run.Runs("p1") == 1 && rig.Clock.ActiveTimers == 1, "dying watches still permit one poll");
        Require(rig.Watches.Created == 4, "one immediate rebuild, then bounded watch backoff");
        Require(rig._store.OpenNotices("p1").Count(n => n.ReasonKey == "autosync-watch-unavailable") == 1,
            "one deduplicated watch failure notice");
        rig.Clock.Advance(Settings.AutoRetryDelay);
        await Until(() => rig.Watches.Created >= 6 && rig.Clock.ActiveTimers == 1, "watch retry uses shared backoff");
        rig.Clock.Advance(SafetyPoll);
        await Until(() => rig.Run.Runs("p1") >= 2 && rig.Clock.ActiveTimers == 1, "periodic sync continues while watches fail");
        Require(rig._store.OpenNotices("p1").Count(n => n.ReasonKey == "autosync-watch-unavailable") == 1,
            "repeated watch failure does not duplicate notice");
        Pass("DyingWatches_BoundedRebuilds");
    }

    private static async Task SlowCancelWatches_NeverOverlapAsync()
    {
        await using var rig = new Rig(["p1"]);
        rig.Watches.SlowCancel = true;
        await rig.StartAsync();
        await rig.ParkedAsync(AutoStatus.Idle);
        for (var round = 0; round < 3; round++)
        {
            rig.WatchA.Signal();
            await rig.ParkedAsync(AutoStatus.Waiting);
            rig.WatchB.Signal();
            await rig.ParkedAsync(AutoStatus.Waiting);
            Snapshot("before-advance");
            rig.Clock.Advance(Settings.ChangeQuietWindow);
            Snapshot("after-advance");
            try { await rig.ParkedAsync(AutoStatus.Idle); }
            catch { Snapshot("idle-timeout"); throw; }

            void Snapshot(string phase) => Console.Error.WriteLine(
                $"SLOW-CANCEL round={round} phase={phase} status={rig.Status("p1")} runs={rig.Run.Runs("p1")} " +
                rig.Clock.DiagnosticSnapshot() + " A=[" + rig.WatchA.DiagnosticSnapshot() +
                "] B=[" + rig.WatchB.DiagnosticSnapshot() + "]");
        }
        Require(rig.Run.Runs("p1") == 4, "each round runs once");
        Require(rig.Watches.All.All(watch => watch.Overlaps == 0),
            "losing waits are joined before the next wait, even when their cancellation finishes late");
        Pass("SlowCancelWatches_NeverOverlap");
    }

    private static async Task RealRunGate_PausedQueuedNeverScansAsync()
    {
        using var store = new InMemoryStateStore();
        var provider = new FakeEndpointProvider();
        var block = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gates = new Dictionary<string, GatedInventory>(StringComparer.Ordinal);
        foreach (var id in new[] { "p1", "p2" })
        {
            var pair = RigPair(id, enabled: true);
            store.SavePair(pair);
            store.MarkFirstSyncDone(id);
            var a = new FakeEndpoint(Side.A, pair.A.RootId);
            gates[id] = new GatedInventory(a) { Block = block };
            provider.Add(pair.A, a).Add(pair.B, new FakeEndpoint(Side.B, pair.B.RootId)).AddInventory(pair.A, gates[id]);
        }
        var run = new SyncRun(store, new Executor(store, Settings, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage), provider, Settings,
            TimeProvider.System);
        var clock = new ManualClock();
        string? pausedPair = null;
        var auto = new AutoSync(store, run, new FakeChangeWatchSource(), Settings,
            pair => pair.PairId == Volatile.Read(ref pausedPair), () => SafetyPoll, clock);
        AutoStatus? Status(string id) => auto.States.FirstOrDefault(state => state.PairId == id)?.Status;
        auto.Start();
        await Until(() => Status("p1") == AutoStatus.Waiting && Status("p2") == AutoStatus.Waiting &&
                          clock.ActiveTimers == 2, "both pairs settle");
        clock.Advance(Settings.ChangeQuietWindow);
        // D75/D77 supersede the whole-engine queue: independent pairs both enter inventory.
        await Until(() => gates["p1"].Reads == 1 && gates["p2"].Reads == 1,
            "independent pairs scan concurrently under real admission");
        using var queuedStop = new CancellationTokenSource();
        var queued = run.IncrementalAsync("p1", new RecordingProgress(), queuedStop.Token);
        queuedStop.Cancel();
        try { await queued; throw new InvalidOperationException("withdrawn same-pair run started"); }
        catch (OperationCanceledException) { }
        Require(gates["p1"].Reads == 1, "withdrawn same-pair queued run performs zero additional scans");
        Volatile.Write(ref pausedPair, "p1");
        auto.Kick("p1");
        await Until(() => Status("p1") == AutoStatus.Paused, "pause promptly cancels blocked inventory");
        Require(gates["p2"].Reads == 1 && Status("p2") == AutoStatus.Running,
            "pausing one pair does not stop the other");
        await auto.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Require(!block.Task.IsCompleted, "stop reaches every blocked worker without releasing its test gate");
        Pass("RealRunGate_PausedQueuedNeverScans");
    }
}
