namespace Massdex.SyncShark.V2;

internal static partial class RunChecks
{
    internal static async Task HistoryThrottleRecoveryAsync(string root)
    {
        foreach (var proof in new[] { "intact", "absent", "unreadable", "changed", "content", "replaced", "archive" })
        {
            using var store = SqliteStateStore.Open(Path.Combine(root, "history-throttle-" + proof),
                Settings, TimeProvider.System, LocalItems.CaptureOptionalHash);
            var fx = Build(suppliedStore: store);
            var original = fx.A.Seed("victim.txt", "original");
            var victim = fx.B.Seed("victim.txt", "original");
            var clock = new CompletionClock();
            var run = new SyncRun(store, fx.Executor, fx.Provider, Settings, clock);
            await FirstSyncAsync(run, fx);
            await fx.A.DeleteAsync(new SyncAction("remove-source", 6, ActionKind.Delete, Side.A,
                original.Path, null, null, original, false, null), default);
            var writer = new HistoryThrottleWriter(fx.B, proof);
            fx.Provider.AddWriter(fx.Pair.B, writer);
            var result = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Require(writer.Deletes == 1 && store.Recoveries(fx.Pair.PairId).Count > 0 &&
                store.Baselines(fx.Pair.PairId).Count == 1 &&
                (await fx.B.ObserveAsync(victim.Path, default))!.ObjectId == victim.ObjectId,
                proof + " keeps the receipt, baseline and victim without replaying a refused delete");
            if (proof != "intact")
            {
                Require(result.Uncertain == 1 && store.UnresolvedIntents(fx.Pair.PairId).Count == 1,
                    proof + " cannot retire a recovery-first intent without intact history-target proof");
                continue;
            }
            Require(result.Skipped == 1 && result.Uncertain == 0 && result.Failed == 0 &&
                store.UnresolvedIntents(fx.Pair.PairId).Count == 0,
                "a definite throttle with intact history target stays skipped, not failed or uncertain");
            var retry = store.SyncRetryAt(fx.Pair.PairId, victim.Path);
            Require(retry is { Attempts: > 0 } && retry.RetryAfter > clock.Now &&
                store.OpenNotices(fx.Pair.PairId).Any(n => n.Path == victim.Path && NoticeChoices.IsRetryNotice(n)),
                "the skipped delete has durable bounded retry debt and an actionable notice");
            writer.Throttle = false;
            clock.Now = retry!.RetryAfter.AddTicks(1);
            var completed = await run.IncrementalAsync(fx.Pair.PairId, new RecordingProgress(), default);
            Require(completed.Done == 1 && writer.Deletes == 2 &&
                store.SyncRetryAt(fx.Pair.PairId, victim.Path) is null &&
                !store.OpenNotices(fx.Pair.PairId).Any(n => n.Path == victim.Path && NoticeChoices.IsRetryNotice(n)) &&
                await fx.B.ObserveAsync(victim.Path, default) is null,
                "only a later due run retries once and clears the retry notice on success");
        }
    }

    private sealed class HistoryThrottleWriter(FakeEndpoint inner, string proof) : IEndpointWriter
    {
        internal int Deletes;
        internal bool Throttle = true;
        public Task<RecoveryRef> PreserveAsync(ItemState victim, string actionId, CancellationToken ct) =>
            Task.FromResult(new RecoveryRef(Side.B, proof == "archive" ? "memory://retained" : victim.Path,
                victim.VersionId, victim.Hash!, victim.Size));
        public async Task DeleteAsync(SyncAction action, CancellationToken ct)
        {
            Deletes++;
            if (Throttle) throw new ProviderWriteThrottledException("The delete was throttled; retry on a later run.",
                new IOException("fixture definite rejection"));
            await inner.DeleteAsync(action, ct);
        }
        public async Task<ItemState?> ObserveAsync(string path, CancellationToken ct)
        {
            var current = await inner.ObserveAsync(path, ct);
            if (Deletes == 0 || !Throttle || current is null) return current;
            return proof switch
            {
                "absent" => null,
                "unreadable" => throw new IOException("fixture metadata unavailable"),
                "changed" => current with { VersionId = "changed" },
                "content" => current with { Hash = "changed" },
                "replaced" => current with { ObjectId = "id:replacement" },
                _ => current
            };
        }
        public Task<ItemState> PutAsync(SyncAction action, Stream content, CancellationToken ct) => inner.PutAsync(action, content, ct);
        public Task<ItemState> MoveAsync(SyncAction action, CancellationToken ct) => inner.MoveAsync(action, ct);
        public Task<ItemState> MkDirAsync(string path, CancellationToken ct) => inner.MkDirAsync(path, ct);
        public Task<Stream> OpenReadAsync(ItemState source, CancellationToken ct) => inner.OpenReadAsync(source, ct);
    }
}
