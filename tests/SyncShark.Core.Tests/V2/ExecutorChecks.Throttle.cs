namespace Massdex.SyncShark.V2;

internal static partial class ExecutorChecks
{
    internal static async Task ThrottleReleasesWorkerAsync()
    {
        var clock = new ThrottleClock();
        var admission = new SyncTransferResourceController(new(MaximumConcurrentFiles: 2, ResourceMode: "Performance"),
            clock, () => new SyncResourcePressure(.1, .4, 8L * 1024 * 1024 * 1024));
        using var store = SqliteStateStore.Open(Path.Combine(Path.GetTempPath(), "throttle-" + Guid.NewGuid().ToString("N")),
            StorageChecks.Fast, clock, LocalItems.CaptureOptionalHash);
        store.SavePair(Pair());
        var retry = FailureRetryOwner.SaveFailedRetry(store, Pair().PairId, "first", "Provider cooldown", null,
            clock.GetUtcNow(), FailureRetryOwner.FailureKind.ProviderThrottle, clock.GetUtcNow().AddMinutes(1));
        var notice = FailureRetryOwner.RetryNotice(Pair().PairId, "first", retry.LastError, retry.Attempts);
        store.AddNotices([notice, notice]);
        Require(store.OpenNotices(Pair().PairId).Count == 1, "throttle notice is deduplicated in SQLite");
        var a = new FakeEndpoint(Side.A, "ra");
        var b = new FakeEndpoint(Side.B, "rb");
        var executor = new Executor(store, Settings(1), admission,
            isStateDamage: StorageFailureClassifier.IsStateDamage, isStoreException: StorageFailureClassifier.IsStoreException);
        var progressed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parked = false;
        var writer = new RecordingWriter(b)
        {
            BeforePut = async (action, ct) =>
            {
                if (action.Path == "second") { progressed.TrySetResult(); return; }
                var lease = SyncTransferResourceController.Lease.Current.Value!;
                await admission.WaitForCooldownAsync(TimeSpan.FromSeconds(2), "read", async (delay, token) =>
                {
                    parked = !lease.Holding;
                    await progressed.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
                    clock.Advance(delay);
                }, ct);
                Require(lease.Holding, "executor action re-admitted after cooldown");
            }
        };
        var results = await Apply(executor, Plan([
            Act(ActionKind.CopyNew, Side.B, "first", source: a.Seed("first", "a"), key: "first"),
            Act(ActionKind.CopyNew, Side.B, "second", source: a.Seed("second", "b"), key: "second")]), a, writer);
        Require(parked && progressed.Task.IsCompletedSuccessfully && results.All(r => r.Outcome == Outcome.Done),
            "a single executor worker releases its gate during cooldown so independent work completes");
        Require(admission.Snapshot().ActiveFiles == 0, "executor cooldown accounting is balanced");
        // The run's existing successful-result owner clears retry debt after verified Done; the store
        // closes that retry notice atomically, rather than introducing a throttle-specific closer.
        store.ClearSyncRetry(Pair().PairId, "first");
        Require(store.OpenNotices(Pair().PairId).Count == 0, "verified success closes the throttle retry notice");

        var deadline = clock.GetUtcNow().AddMinutes(1);
        writer.BeforePut = (_, _) => Task.FromException(new ProviderThrottleExhaustedException(deadline, "retry-bound", "read"));
        var failedAction = Act(ActionKind.CopyNew, Side.B, "third", source: a.Seed("third", "c"), key: "third");
        var failed = (await Apply(executor, Plan(failedAction), a, writer)).Single();
        Require(failed.Outcome == Outcome.Failed && failed.FailureKind == FailureRetryOwner.FailureKind.ProviderThrottle &&
            failed.RetryNotBefore == deadline, "executor retains typed throttle cause and retry deadline without marking the item synced");
        var durable = FailureRetryOwner.SaveFailedRetry(store, Pair().PairId, "third", failed.Reason, null,
            clock.GetUtcNow(), failed.FailureKind, failed.RetryNotBefore);
        Require(durable.RetryAfter >= deadline && durable.FailureKind == FailureRetryOwner.FailureKind.ProviderThrottle,
            "executor failure reaches durable shared retry state unchanged");
    }

    private sealed class ThrottleClock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(_ticks);
        internal void Advance(TimeSpan delay) => _ticks += delay.Ticks;
    }
}
