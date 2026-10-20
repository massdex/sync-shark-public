using System.Text;

namespace Massdex.SyncShark.V2;

internal static partial class ExecutorChecks
{
    private static void Admission_ActualSizeReservations()
    {
        var controller = new SyncTransferResourceController(new SyncTransferSettings(ResourceMode: "Performance"),
            TimeProvider.System, () => new SyncResourcePressure(0, .95, 258L * 1024 * 1024));
        var scope = controller.ForEndpoints([]);
        var leases = Enumerable.Range(0, 32).Select(_ => scope.TryAcquire(16, true)).ToArray();
        Require(leases.All(x => x is not null), "2 MiB above reserve admits 32 tiny files at a 64 KiB floor");
        Require(scope.TryAcquire(16, true) is null, "tiny reservations remain bounded by available memory");
        foreach (var lease in leases) lease!.Complete(true);
        var large = scope.TryAcquire(1L << 30, true)!;
        Require(large.ReservedBytes == 16L * 1024 * 1024, "large streamed files reserve only the bounded buffer cap");
        large.Complete(true);
    }

    public static Task RunUploadLatencyAsync() => UploadPipeline_2000TinyFiles(latency: true);

    public static async Task RunUploadWorkerComparisonAsync()
    {
        await UploadPipeline_2000TinyFiles(latency: true, workers: 64);
        await UploadPipeline_2000TinyFiles(latency: true, workers: 128);
    }

    private static async Task UploadPipeline_2000TinyFiles(bool latency = false, int? workers = null)
    {
        const int files = 2000, batchSize = 1000;
        var bytes = Encoding.UTF8.GetBytes(new string('x', 16));
        // FakeEndpoint uses SHA256; the scripted transport returns the source's retained hash.
        var source = new FakeEndpoint(Side.A, "ra");
        var actions = Enumerable.Range(0, files).Select(i => Act(ActionKind.CopyNew, Side.B,
            "f" + i, source: source.Seed("f" + i, bytes), key: "k" + i)).ToArray();
        using var wire = new UploadBatchWire(actions[0].Source!.Hash!) { HoldFirstCommit = !latency, ExpectedStarts = files,
            StartLatency = latency ? TimeSpan.FromMilliseconds(150) : TimeSpan.Zero,
            FinishLatency = latency ? TimeSpan.FromMilliseconds(300) : TimeSpan.Zero };
        using var http = new HttpClient(wire);
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "account-id", "root-namespace", "home-namespace", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes);
        var client = new DropboxPilotClient(http, new FixedTokenSource(), "/Pilot", binding, DropboxWire.RootId);
        var settings = latency ? V2Settings.Default : Settings(4) with { PendingUploadCommitLimit = batchSize, UploadBatchSize = batchSize,
            UploadBatchMaxAge = TimeSpan.FromSeconds(2) };
        if (workers is { } cap) settings = settings with { MaxWorkers = cap };
        var store = new InMemoryStateStore(); store.SavePair(Pair());
        var admission = Admission();
        var writer = new DropboxWriter(new RootDef("rb", "Dropbox", "acc", "/Pilot", DropboxWire.RootId),
            Side.B, client, "unused", settings);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var work = Apply(new Executor(store, settings, admission), Plan(actions), source, writer);
        if (!latency)
        {
            await wire.FirstCommit.Task.WaitAsync(HeldEffectTimeout);
            await wire.AllStarted.Task.WaitAsync(HeldEffectTimeout);
            async Task WaitForStagedAsync()
            {
                while (admission.Snapshot().ActiveTransfers != 0) await Task.Yield();
            }
            await WaitForStagedAsync().WaitAsync(HeldEffectTimeout);
            Require(wire.Starts == files, "the next 1000 sessions stage with four workers while the first commit is held");
            Require(admission.Snapshot().ActiveTransfers <= 4, "pending commits do not retain transfer admissions");
            wire.ReleaseCommit.TrySetResult();
        }
        var results = await work.WaitAsync(latency ? TimeSpan.FromSeconds(30) : HeldEffectTimeout);
        if (latency) Console.WriteLine($"upload latency workers={settings.MaxWorkers}: {files / clock.Elapsed.TotalSeconds:F1} files/s; batches [{string.Join(",", wire.BatchSizes)}]; elapsed {clock.Elapsed.TotalSeconds:F3}s");
        Require(results.Count == files && results.All(x => x.Outcome == Outcome.Done), "2000 tiny uploads succeed");
        Require(latency ? wire.BatchSizes.Sum() == files && wire.BatchSizes.All(n => n <= batchSize)
            : wire.BatchSizes.SequenceEqual(new[] { batchSize, batchSize }),
            "batches reach near configured size rather than worker cap");
        Require(wire.PeakFinishes <= settings.UploadCommitConcurrency && wire.SequentialClosed,
            "finish commits stay within namespace concurrency and tiny sessions are sequential and closed");
        Require(store.UnresolvedIntents(Pair().PairId).Count == 0, "every result durably closes its intent");
    }

    private static async Task UploadPipeline_LostAckRetainsCheckpoint()
    {
        var source = new FakeEndpoint(Side.A, "ra");
        var actions = Enumerable.Range(0, 4).Select(i => Act(ActionKind.CopyNew, Side.B, "f" + i,
            source: source.Seed("f" + i, new string('x', 16)), key: "k" + i)).ToArray();
        using var wire = new UploadBatchWire(actions[0].Source!.Hash!) { LoseAcknowledgement = true };
        using var http = new HttpClient(wire);
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "account-id", "root-namespace", "home-namespace", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes);
        var client = new DropboxPilotClient(http, new FixedTokenSource(), "/Pilot", binding, DropboxWire.RootId);
        var settings = Settings(2) with { UploadBatchSize = 4, UploadBatchMaxAge = TimeSpan.FromSeconds(2) };
        var writer = new DropboxWriter(new RootDef("rb", "Dropbox", "acc", "/Pilot", DropboxWire.RootId),
            Side.B, client, "unused", settings);
        using var store = new InMemoryStateStore(); store.SavePair(Pair());
        var results = await Apply(new Executor(store, settings, Admission()), Plan(actions), source, writer).WaitAsync(HeldEffectTimeout);
        var intents = store.UnresolvedIntents(Pair().PairId);
        Require(results.All(r => r.Outcome == Outcome.Uncertain) && intents.Count == 4,
            "lost finish acknowledgement stays uncertain despite an absent observation");
        Require(intents.All(i => i.ProviderEffect?.Kind == "upload-commit"), "every unresolved upload retains its commit checkpoint");
        Require(wire.Finishes == 1, "executor never replays an uncertain upload");
    }

    private static async Task UploadPipeline_IsolatedFailureAndLostAck()
    {
        await UploadPipeline_LostAckRetainsCheckpoint();
        foreach (var loseAck in new[] { false, true })
        {
            var bytes = Encoding.UTF8.GetBytes(new string('x', 16));
            using var wire = new UploadBatchWire(DropboxContentHash.Compute(bytes))
                { RejectOne = !loseAck, LoseAcknowledgement = loseAck };
            using var http = new HttpClient(wire);
            var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
                "account-id", "root-namespace", "home-namespace", DropboxProviderConfiguration.ContentAccess,
                DropboxProviderConfiguration.RequiredScopes);
            var client = new DropboxPilotClient(http, new FixedTokenSource(), "/Pilot", binding, DropboxWire.RootId);
            var settings = Settings(2) with { UploadBatchSize = 4, UploadBatchMaxAge = TimeSpan.FromSeconds(2) };
            var tasks = Enumerable.Range(0, 4).Select(async i =>
            {
                using var stream = new MemoryStream(bytes);
                try { await client.UploadBatchedAsync("f" + i, stream, DropboxContentHash.Compute(bytes), null, settings, default); return "done"; }
                catch (DropboxEffectUncertainException) { return "uncertain"; }
                catch (DropboxApiException) { return "rejected"; }
            }).ToArray();
            var results = await Task.WhenAll(tasks).WaitAsync(HeldEffectTimeout);
            Require(wire.Finishes == 1, "an uncertain finish is never replayed");
            Require(loseAck ? results.All(x => x == "uncertain") : results.Count(x => x == "done") == 3 && results.Count(x => x == "rejected") == 1,
                "lost acknowledgement remains uncertain; an explicit entry failure is isolated");
        }
    }

    // The batch cap is a product default (now the 1000-entry provider maximum): a burst must never send a finish batch
    // larger than the cap, and a full burst of the cap must fill one batch to it.
    private static async Task UploadPipeline_DefaultBatchCapAsync()
    {
        var files = V2Settings.Default.UploadBatchSize;
        var bytes = Encoding.UTF8.GetBytes(new string('x', 16));
        var hash = DropboxContentHash.Compute(bytes);
        using var wire = new UploadBatchWire(hash) { ExpectedStarts = files };
        using var http = new HttpClient(wire);
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "account-id", "root-namespace", "home-namespace", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes);
        var client = new DropboxPilotClient(http, new FixedTokenSource(), "/Pilot", binding, DropboxWire.RootId);
        // Default settings only: the cap under test is the product default. A generous age keeps the default 5 s age
        // from flushing a partial batch mid-arrival (staging the cap under load can outlast the short default), which is
        // the only way this test could ever see the batch split below the cap.
        var settings = V2Settings.Default with { UploadBatchMaxAge = TimeSpan.FromSeconds(2) };
        var tasks = Enumerable.Range(0, files).Select(async i =>
        {
            using var stream = new MemoryStream(bytes);
            await client.UploadBatchedAsync("f" + i, stream, hash, null, settings, default);
        }).ToArray();

        await Task.WhenAll(tasks).WaitAsync(HeldEffectTimeout);
        Require(wire.BatchSizes.Sum() == files && wire.BatchSizes.All(n => n <= V2Settings.Default.UploadBatchSize),
            "every finish batch stays within the entry cap: " + string.Join(",", wire.BatchSizes));
        Require(wire.BatchSizes.Contains(V2Settings.Default.UploadBatchSize),
            "the queue fills a batch to the cap: " + string.Join(",", wire.BatchSizes));
    }

    // The explicit batch timeout is floored for short batches (never a fraction of a second) and the transport client
    // timeout is configured above it, never the implicit 100-second HttpClient default.
    // (No "a slow finish completes instead of failing at 100 s" behaviour test exists on purpose: with the transport
    // timeout above the batch timeout the unfixed path, bounded only by the transport, is at least as permissive, so
    // such a test cannot fail on the base; the timeout-to-Uncertain test below is the one that does.)
    private static void UploadPipeline_BatchTimeoutPolicy()
    {
        var settings = V2Settings.Default;
        // A full batch gets the base plus the per-entry allowance for every entry it can hold.
        var full = settings.BatchOperationTimeout + TimeSpan.FromTicks(settings.BatchTimeoutPerEntry.Ticks * 250);
        Require(settings.BatchOperationTimeoutFor(250, 250) == full,
            "a full batch gets the base operation timeout plus the per-entry allowance");
        Require(settings.BatchOperationTimeoutFor(1, 250) == TimeSpan.FromMinutes(1),
            "a single entry is floored at one minute, not a fraction of a second");
        Require(settings.BatchOperationTimeoutFor(1, 250) == settings.BatchOperationTimeoutFor(2, 250),
            "the floor keeps tiny batches at the same bound");
        Require(settings.BatchOperationTimeoutFor(500, 250) == full,
            "a batch larger than the cap never exceeds the full-batch budget");
        var transport = EngineComposition.DropboxHttpTimeoutFor(settings);
        // The transport sits above the largest possible batch bound (a full upload or delete batch), never the base.
        var maximumBatch = Math.Max(settings.UploadBatchSize, settings.DeleteBatchSize);
        Require(transport > settings.BatchOperationTimeoutFor(maximumBatch, maximumBatch) &&
                settings.BatchOperationTimeout > TimeSpan.FromSeconds(100),
            $"the transport timeout ({transport.TotalMinutes:N1} min) sits above the full-batch bound " +
            $"({settings.BatchOperationTimeoutFor(maximumBatch, maximumBatch).TotalMinutes:N1} min), which is above the old implicit 100 s default");
    }

    // A one-entry batch whose finish is slow relative to a per-entry share still completes: the floor keeps a short
    // batch's timeout at the smaller of one minute and the full timeout, not the unfloored per-entry fraction.
    private static async Task UploadPipeline_SmallBatchFloorAsync()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('x', 16));
        var hash = DropboxContentHash.Compute(bytes);
        using var wire = new UploadBatchWire(hash) { FinishLatency = TimeSpan.FromMilliseconds(300) };
        using var http = new HttpClient(wire) { Timeout = TimeSpan.FromSeconds(30) };
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "account-id", "root-namespace", "home-namespace", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes);
        var client = new DropboxPilotClient(http, new FixedTokenSource(), "/Pilot", binding, DropboxWire.RootId);
        // One entry out of a 250 cap: an unfloored per-entry share would be 2 s / 250 = 8 ms and cut this 300 ms finish.
        var settings = V2Settings.Default with { UploadBatchSize = 250, UploadBatchMaxAge = TimeSpan.FromMilliseconds(50),
            BatchOperationTimeout = TimeSpan.FromSeconds(2) };
        using var stream = new MemoryStream(bytes);
        await client.UploadBatchedAsync("f0", stream, hash, null, settings, default).WaitAsync(HeldEffectTimeout);
        Require(wire.Finishes == 1, "the slow one-entry finish completes under the floored batch timeout");
    }

    // A finish that outstays the explicit per-batch timeout is Unknown, never replayed, and every entry's outcome
    // carries the reason (the body is bounded by the explicit token, not left unbounded by CancellationToken.None).
    private static async Task UploadPipeline_BatchTimeoutIsUncertainAsync()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('x', 16));
        var hash = DropboxContentHash.Compute(bytes);
        using var wire = new UploadBatchWire(hash) { FinishLatency = TimeSpan.FromSeconds(5) };
        using var http = new HttpClient(wire) { Timeout = TimeSpan.FromSeconds(30) };
        var binding = new DropboxConnectionBinding(DropboxProviderConfiguration.RegisteredClientId,
            "account-id", "root-namespace", "home-namespace", DropboxProviderConfiguration.ContentAccess,
            DropboxProviderConfiguration.RequiredScopes);
        var client = new DropboxPilotClient(http, new FixedTokenSource(), "/Pilot", binding, DropboxWire.RootId);
        var settings = V2Settings.Default with { UploadBatchSize = 4, UploadBatchMaxAge = TimeSpan.FromMilliseconds(50),
            BatchOperationTimeout = TimeSpan.FromMilliseconds(300) };
        var tasks = Enumerable.Range(0, 4).Select(async i =>
        {
            using var stream = new MemoryStream(bytes);
            try { await client.UploadBatchedAsync("f" + i, stream, hash, null, settings, default); return ("done", (string?)null); }
            catch (DropboxEffectUncertainException error) { return ("uncertain", error.Message); }
        }).ToArray();

        var results = await Task.WhenAll(tasks).WaitAsync(HeldEffectTimeout);
        Require(wire.Finishes == 1, "a timed-out batch is never replayed");
        Require(results.All(result => result.Item1 == "uncertain"),
            "every entry of a timed-out batch is Uncertain: " + string.Join(",", results.Select(result => result.Item1)));
        Require(results.All(result => result.Item2!.Contains("unknown", StringComparison.Ordinal)),
            "each Uncertain outcome carries the unknown-outcome reason: " + string.Join(" | ", results.Select(result => result.Item2)));
    }
}
