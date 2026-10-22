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
        Require(rig._store.OpenNotices("p1").Count == 0, "one transient failure is silent");
        for (var i = 1; i < 4; i++)
        {
            var expected = 3 + i;
            rig.Clock.Advance(rig.State("p1").RetryAt!.Value - rig.Clock.GetUtcNow());
            await Until(() => rig.Run.Runs("p1") == expected && rig.State("p1").RetryAt > rig.Clock.GetUtcNow() && rig.Clock.ActiveTimers == 1, "failed run retries");
        }
        var notices = rig._store.OpenNotices("p1");
        Require(notices.Count == 1 && NoticeChoices.Options(notices[0]).Select(o => o.Choice).SequenceEqual(new[] { NoticeChoices.RetryNow, NoticeChoices.Ignore }),
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
        Require(notice.ReasonKey == "autosync-failed" && notice.Detail.Contains("Reconnect", StringComparison.Ordinal),
            "first auth failure immediately explains the reconnect fix");
        Pass("ImmediateAccessNotice");
        foreach (var error in new Exception[] { new EndpointAccessException("completion access lost"), new StateDamagedException("completion store damaged") })
        {
            await using var completion = new Rig(["p1"]);
            completion.Run.Then(FakeSyncRun.Report("p1", RunOutcomes.Partial, 0) with { Done = 1, Uploaded = 1,
                CompletionFailed = true, HaltedBy = error });
            await completion.StartAsync();
            await completion.ParkedAsync(AutoStatus.Held);
            Require(completion._store.OpenNotices("p1").Single().Detail.Contains(error is EndpointAccessException ? "Reconnect" : "verified backup"),
                "counted Partial holds pair and raises correct authority notice in same run");
        }
    }

    internal static async Task ImmediateDiskFullReportAsync()
    {
        await using var rig = new Rig(["p1"]);
        rig.Run.Then(FakeSyncRun.Report("p1", RunOutcomes.Failed, 0,
            new ActionResult("effect", Outcome.Failed, null, null, null, "disk full")
                { FailureHResult = unchecked((int)0x80070070) }));
        await rig.StartAsync();
        await rig.ParkedAsync(AutoStatus.Waiting);
        Require(rig._store.OpenNotices("p1").Single().Detail.Contains("disk-full", StringComparison.Ordinal),
            "a failed report preserves disk-full classification and raises it immediately");
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
            Require(rig._store.OpenNotices("p1").Count == 0, "each cause change preserves Ignore in one outage");
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
            await Until(() => arming.Status("p1") == AutoStatus.Held, "a cancelled arm holds instead of ending the loop");
            Require(arming.State("p1").Reason?.Key == AutoSyncReasons.WatchUnavailable.Key, "held as watch-unavailable");
        }
        Pass("ForeignCancellation_Retries");
    }

    private static async Task AccessFault_RetriesAtSafetyPollAsync()
    {
        await using var rig = await Rig.StartedAsync();
        rig.WatchA.Fail(new EndpointAccessException("revoked", new HttpRequestException("401")));
        await Until(() => rig.Status("p1") == AutoStatus.Held, "a watch that lost access holds the pair");
        Require(rig.State("p1").Reason?.Key == AutoSyncReasons.AccountAccess.Key,
            "the access error survives unwrapping (not its inner provider error)");
        await Until(() => rig.Clock.ActiveTimers == 1, "auth hold has a safety timer");
        rig.Clock.Advance(SafetyPoll);

        await rig.ParkedAsync(AutoStatus.Waiting);
        rig.Clock.Advance(Settings.ChangeQuietWindow);
        await rig.ParkedAsync(AutoStatus.Idle);
        Require(rig.Run.Runs("p1") == 2, "auth hold automatically retries at the safety poll");
        Pass("AccessFault_RetriesAtSafetyPoll");
    }

    private static async Task DyingWatches_BoundedRebuildsAsync()
    {
        await using var rig = new Rig(["p1"]);
        rig.Watches.WaitError = new IOException("watcher dies at once");
        rig.Auto.Start();
        await Until(() => rig.Status("p1") == AutoStatus.Held, "watches that keep dying hold the pair");
        Require(rig.Watches.Created == 4, "one immediate rebuild, then the hold (no tight loop)");
        Require(rig.State("p1").Reason?.Key == AutoSyncReasons.WatchUnavailable.Key, "held as watch-unavailable");
        await Until(() => rig.Clock.ActiveTimers == 1, "the hold waits for the safety poll");
        rig.Clock.Advance(SafetyPoll);
        await Until(() => rig.Watches.Created == 6 && rig.Status("p1") == AutoStatus.Held && rig.Clock.ActiveTimers == 1,
            "each safety poll tries once more");
        Require(rig.Run.Entered.IsEmpty, "no run while the watches cannot stay alive");
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
            rig.Clock.Advance(Settings.ChangeQuietWindow);
            await rig.ParkedAsync(AutoStatus.Idle);
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
        var run = new SyncRun(store, new Executor(store, Settings, ExecutorChecks.Admission()), provider, Settings,
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
        await Until(() => gates["p1"].Reads + gates["p2"].Reads == 1 &&
                          Status("p1") == AutoStatus.Running && Status("p2") == AutoStatus.Running,
            "one pair scans inside the real gate, the other is queued");
        var queued = gates["p1"].Reads == 0 ? "p1" : "p2";
        var other = queued == "p1" ? "p2" : "p1";
        Volatile.Write(ref pausedPair, queued);
        auto.Kick(queued);
        await Until(() => Status(queued) == AutoStatus.Paused, "the queued pair shows Paused");
        block.SetResult();
        await Until(() => Status(other) == AutoStatus.Idle, "the admitted run finishes");
        Require(gates[queued].Reads == 0, "SyncRun's admission keeps a paused queued run from ever scanning");
        await auto.StopAsync();
        Pass("RealRunGate_PausedQueuedNeverScans");
    }
}
