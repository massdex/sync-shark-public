namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    private static async Task DispatchAbsenceAsync()
    {
        foreach (var scenario in new[] { "one", "all", "unknown" })
        {
            using var h = new Harness();
            var folder = Path.Combine(_root, "dispatch-" + scenario + "-" + Guid.NewGuid().ToString("N"));
            var local = new FakeEndpoint(Side.A, "local") { DropboxHashes = true };
            var source = new RecordingWriter(local);
            var settings = V2Settings.Default with { DeleteBatchSize = 3, DeleteBatchMaxAge = TimeSpan.FromSeconds(30) };
            var remote = new DropboxWriter(Root(), Side.B, h.Client, Path.Combine(folder, "spool"), settings);
            var target = new QueuedDeleteWriter(remote, 3);
            var actions = new List<SyncAction>();
            var pair = new PairDef("dispatch", "Pair A", new("local", SyncProviders.Local, null, "/local", "local"),
                Root(), [], [], true);
            using (var store = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System))
            {
                store.SavePair(pair);
                for (var i = 0; i < 3; i++)
                {
                    var path = "file-" + i;
                    var original = local.Seed(path, "same");
                    h.Wire.SeedFile(path, "same");
                    var victim = (await remote.ObserveAsync(path, default))!;
                    var adopt = new SyncAction("adopt-" + i, 12, ActionKind.Adopt, Side.B, path, null,
                        original, victim, false, "key-" + i);
                    store.CommitResult(pair.PairId, 1, adopt, new(adopt.ActionId, Outcome.Done, original, victim, null, null));
                    await local.DeleteAsync(new("remove-" + i, 6, ActionKind.Delete, Side.A, path, null, null, original, false, null), default);
                    actions.Add(new("delete-" + i, 6, ActionKind.Delete, Side.B, path, null, null, victim, true, "key-" + i));
                }
                var binding = Binding().Normalize();
                var gate = DropboxDeleteJobGate.For(binding.ConnectionId, binding.RootNamespaceId);
                gate.Throttled(); gate.Throttled();
                await gate.EnterAsync();
                var executor = new Executor(store, settings, ExecutorChecks.Admission());
                var running = CollectAsync();
                try
                {
                    await target.Queued.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    Require(!h.Wire.Calls.Contains("/2/files/delete_batch"), "job gate blocks submission after initial absence admission");
                    if (scenario == "unknown") source.AbsenceUnknown = true;
                    else for (var i = 0; i < (scenario == "all" ? 3 : 1); i++) local.Seed("file-" + i, "recreated");
                }
                finally { gate.Exit(false); }
                var results = await running.WaitAsync(TimeSpan.FromSeconds(30));
                var blocked = scenario == "one" ? 1 : 3;
                Require(results.Count(r => r.DeleteSourceUnconfirmed) == blocked,
                    "dispatch returns the existing source guard result for present and unknown sources");
                Require(h.Wire.DeleteBatchSizes.Sum() == 3 - blocked,
                    "only still-absent members are posted; an entirely blocked batch sends nothing");
                Require(await remote.ObserveAsync("file-0", default) is not null,
                    "recreated or unconfirmed source keeps its remote survivor");

                async Task<List<ActionResult>> CollectAsync()
                {
                    var results = new List<ActionResult>();
                    await foreach (var result in executor.ApplyAsync(new(pair.PairId, 1, actions, []), source, target, default))
                        results.Add(result);
                    return results;
                }
            }
            using var reopened = SqliteStateStore.Open(folder, StorageChecks.Fast, TimeProvider.System);
            Require(reopened.SyncRetryAt(pair.PairId, "file-0") is not null &&
                reopened.Baselines(pair.PairId).Any(b => b.ObjectKey == "key-0") &&
                reopened.UnresolvedIntents(pair.PairId).Count == 0,
                "cold reopen retains blocked correspondence and retry but retires definitely unsubmitted intent");
        }
    }

    private sealed class QueuedDeleteWriter(IEndpointWriter inner, int expected) : IEndpointWriter
    {
        private int _queued;
        internal TaskCompletionSource Queued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task DeleteAsync(SyncAction action, CancellationToken ct)
        {
            var pending = inner.DeleteAsync(action, ct);
            if (Interlocked.Increment(ref _queued) == expected) Queued.TrySetResult();
            return pending;
        }
        public Task<ItemState> PutAsync(SyncAction action, Stream content, CancellationToken ct) => inner.PutAsync(action, content, ct);
        public Task<ItemState> MoveAsync(SyncAction action, CancellationToken ct) => inner.MoveAsync(action, ct);
        public Task<ItemState> MkDirAsync(string path, CancellationToken ct) => inner.MkDirAsync(path, ct);
        public Task<RecoveryRef> PreserveAsync(ItemState victim, string actionId, CancellationToken ct) => inner.PreserveAsync(victim, actionId, ct);
        public Task<ItemState?> ObserveAsync(string path, CancellationToken ct) => inner.ObserveAsync(path, ct);
        public Task<Stream> OpenReadAsync(ItemState source, CancellationToken ct) => inner.OpenReadAsync(source, ct);
    }
}
