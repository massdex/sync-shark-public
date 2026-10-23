namespace Massdex.SyncShark.V2;

internal static partial class ExecutorChecks
{
    private static async Task UploadPipeline_ConcurrentQueueAndTailsAsync()
    {
        Require(V2Settings.Default.UploadCommitConcurrency == 1,
            "default namespace commit concurrency is one (the namespace commit lock is account-wide)");
        foreach (var concurrency in new[] { 1, 2, 4 })
        {
            var settings = V2Settings.Default with { UploadCommitConcurrency = concurrency, UploadBatchSize = 2,
                PendingUploadCommitLimit = 16, UploadBatchMaxAge = TimeSpan.FromMilliseconds(20) };
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var filled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var active = 0;
            var peak = 0;
            var batches = new List<int>();
            var queue = new DropboxUploadQueue(settings, async (batch, _) =>
            {
                var count = Interlocked.Increment(ref active);
                lock (batches) { peak = Math.Max(peak, count); batches.Add(batch.Count); }
                if (count == concurrency) filled.TrySetResult();
                await release.Task;
                foreach (var item in batch) item.Result.TrySetResult(new PilotUploadResult("id", "rev", item.Size, item.ExpectedContentHash));
                Interlocked.Decrement(ref active);
            });
            var items = Enumerable.Range(0, concurrency * 2 + 3)
                .Select(i => new DropboxUploadCommit("f", null, "s" + i, "hash", 1)).ToArray();
            try
            {
                foreach (var item in items) { await queue.Slots.WaitAsync(); queue.Enqueue(item); }
                await filled.Task.WaitAsync(HeldEffectTimeout);
                // Let the tail's timer expire while every dispatch lane is occupied.
                await Task.Delay(TimeSpan.FromMilliseconds(60));
                Require(active == concurrency && batches.Count == concurrency, "queue fills, but never exceeds, its dispatch lanes");
            }
            finally { release.TrySetResult(); }
            await Task.WhenAll(items.Select(i => i.Result.Task)).WaitAsync(HeldEffectTimeout);
            Require(peak == concurrency && batches.Sum() == items.Length && batches.Contains(1),
                "bounded overlapping batches drain the expired short tail without a new arrival");
            Require(queue.Slots.CurrentCount == settings.PendingUploadCommitLimit, "all pending slots are returned");
        }

        // With no full batch or in-flight predecessor, the age timer alone must flush a short tail.
        var aged = new DropboxUploadCommit("f", null, "tail", "hash", 1);
        var tailQueue = new DropboxUploadQueue(V2Settings.Default with { UploadBatchMaxAge = TimeSpan.FromMilliseconds(20) },
            (batch, _) => { foreach (var item in batch) item.Result.TrySetResult(new PilotUploadResult("id", "rev", 1, "hash")); return Task.CompletedTask; });
        await tailQueue.Slots.WaitAsync();
        tailQueue.Enqueue(aged);
        await aged.Result.Task.WaitAsync(HeldEffectTimeout);
        foreach (var invalid in new[] { 0, 5 })
        {
            try { _ = new DropboxUploadQueue(V2Settings.Default with { UploadCommitConcurrency = invalid }, (_, _) => Task.CompletedTask); }
            catch (ArgumentOutOfRangeException) { continue; }
            throw new InvalidOperationException("invalid commit concurrency was accepted");
        }
    }

    private static async Task UploadPipeline_NamespaceGateAsync()
    {
        foreach (var concurrency in new[] { 1, 2, 4 })
        {
            var account = Guid.NewGuid().ToString("N");
            var gate = DropboxUploadCommitGate.For(account, "root", concurrency);
            Require(ReferenceEquals(gate, DropboxUploadCommitGate.For(account, "root", concurrency == 4 ? 1 : 4)),
                "different settings reuse the same namespace gate and its first-bound capacity");
            Require(!ReferenceEquals(gate, DropboxUploadCommitGate.For(account, "other-root", concurrency)),
                "different namespaces have independent capacity");
            for (var i = 0; i < concurrency; i++) await gate.WaitAsync();
            var blocked = gate.WaitAsync();
            Require(!blocked.IsCompleted, "namespace gate bounds concurrent callers");
            gate.Release();
            await blocked.WaitAsync(HeldEffectTimeout);
            gate.Release(concurrency);
            Require(gate.CurrentCount == concurrency, "namespace gate returns every permit");
        }
    }

    private static async Task UploadPipeline_CrossClientConcurrencyAsync()
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(new string('x', 16));
        var hash = DropboxContentHash.Compute(bytes);
        using var wire = new UploadBatchWire(hash) { HoldAllCommits = true, ExpectedConcurrentFinishes = 2 };
        // A private account/namespace keeps this run's commit gate independent of the shared default binding (whose
        // first-bound capacity is now the one-commit default) and its diagnostics off the shared trace.
        wire.AccountId = "acct-" + Guid.NewGuid().ToString("N");
        wire.RootNamespaceId = "ns-" + Guid.NewGuid().ToString("N");
        wire.HomeNamespaceId = "home-" + Guid.NewGuid().ToString("N");
        using var http = new HttpClient(wire);
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            wire.AccountId, wire.RootNamespaceId, wire.HomeNamespaceId, DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes);
        var clients = Enumerable.Range(0, 3).Select(_ =>
            new DropboxPilotClient(http, new FixedTokenSource(), "/Pilot", binding, DropboxWire.RootId)).ToArray();
        // An explicit two (not the one-commit default) proves the shared namespace gate really admits two clients.
        var settings = V2Settings.Default with { UploadBatchSize = 1, UploadCommitConcurrency = 2 };
        var tasks = Enumerable.Range(0, 9).Select(async i =>
        {
            using var stream = new MemoryStream(bytes);
            await clients[i % clients.Length].UploadBatchedAsync("f" + i, stream, hash, null, settings, default);
        }).ToArray();
        try
        {
            await wire.ConcurrentFinishesReached.Task.WaitAsync(HeldEffectTimeout);
            Require(wire.ActiveFinishes == 2, "two different clients commit concurrently within one namespace");
        }
        finally { wire.ReleaseCommit.TrySetResult(); }
        await Task.WhenAll(tasks).WaitAsync(HeldEffectTimeout);
        Require(wire.PeakFinishes == 2 && wire.Finishes == tasks.Length,
            "three clients share two namespace permits and all entries complete");
    }

    // The session-start gate shares one bounded semaphore per account namespace, exactly like the commit gate.
    private static async Task UploadSessionStart_GateAsync()
    {
        Require(V2Settings.Default.UploadSessionStartConcurrency == 32, "default namespace session-start concurrency is 32");
        foreach (var concurrency in new[] { 1, 8, 128 })
        {
            var account = Guid.NewGuid().ToString("N");
            var gate = DropboxUploadSessionStartGate.For(account, "root", concurrency);
            Require(ReferenceEquals(gate, DropboxUploadSessionStartGate.For(account, "root", concurrency == 128 ? 1 : 128)),
                "different settings reuse the same namespace gate and its first-bound capacity");
            Require(!ReferenceEquals(gate, DropboxUploadSessionStartGate.For(account, "other-root", concurrency)),
                "different namespaces have independent capacity");
            for (var i = 0; i < concurrency; i++) await gate.WaitAsync();
            var blocked = gate.WaitAsync();
            Require(!blocked.IsCompleted, "namespace gate bounds concurrent callers");
            gate.Release();
            await blocked.WaitAsync(HeldEffectTimeout);
            gate.Release(concurrency);
            Require(gate.CurrentCount == concurrency, "namespace gate returns every permit");
        }
        foreach (var invalid in new[] { 0, 129 })
        {
            try { _ = DropboxUploadSessionStartGate.For(Guid.NewGuid().ToString("N"), "root", invalid); }
            catch (ArgumentOutOfRangeException) { continue; }
            throw new InvalidOperationException("invalid session-start concurrency was accepted");
        }
    }

    // A burst of uploads to one namespace never issues more simultaneous session starts than its cap.
    private static async Task UploadSessionStart_ConcurrencyCapAsync()
    {
        foreach (var cap in new[] { 1, 4 }) await UploadStartCapRunAsync(cap);
    }

    private static async Task UploadStartCapRunAsync(int cap)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(new string('x', 16));
        var hash = DropboxContentHash.Compute(bytes);
        using var wire = new UploadBatchWire(hash) { StartLatency = TimeSpan.FromMilliseconds(60) };
        // A private account/namespace keeps this run's start gate independent of the shared default binding.
        wire.AccountId = "acct-" + Guid.NewGuid().ToString("N");
        wire.RootNamespaceId = "ns-" + Guid.NewGuid().ToString("N");
        wire.HomeNamespaceId = "home-" + Guid.NewGuid().ToString("N");
        using var http = new HttpClient(wire);
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            wire.AccountId, wire.RootNamespaceId, wire.HomeNamespaceId, DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes);
        var client = new DropboxPilotClient(http, new FixedTokenSource(), "/Pilot", binding, DropboxWire.RootId);
        var settings = V2Settings.Default with { UploadSessionStartConcurrency = cap, UploadBatchSize = 1,
            PendingUploadCommitLimit = 128 };
        var uploads = Enumerable.Range(0, 24).Select(index =>
        {
            var stream = new MemoryStream(bytes);
            return client.UploadBatchedAsync("f" + index, stream, hash, null, settings, default);
        }).ToArray();
        await Task.WhenAll(uploads).WaitAsync(HeldEffectTimeout);
        Require(wire.PeakStarts <= cap, $"concurrent session starts never exceed the cap of {cap}");
        Require(cap == 1 ? wire.PeakStarts == 1 : wire.PeakStarts > 1,
            "the cap allows genuine overlap without exceeding it");
        Require(wire.Starts == 24 && wire.Finishes == 24, "every capped start stages exactly once and commits");
    }
}
