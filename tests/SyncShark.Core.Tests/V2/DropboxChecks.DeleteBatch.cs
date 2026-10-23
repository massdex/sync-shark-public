namespace Massdex.SyncShark.V2;

/// <summary>The batch contract: one racing revision refuses only that entry, and asynchronous jobs are checked.</summary>
internal static partial class DropboxChecks
{
    private static async Task DeleteBatchRaceAsync()
    {
        using var h = new Harness();
        var writer = new DropboxWriter(Root(), Side.B, h.Client, Path.Combine(_root, "spool"),
            V2Settings.Default with { DeleteBatchSize = 2 });
        h.Wire.SeedFile("raced.txt", "keep");
        h.Wire.SeedFile("delete.txt", "remove");
        var raced = (await writer.ObserveAsync("raced.txt", CancellationToken.None))!;
        var deleted = (await writer.ObserveAsync("delete.txt", CancellationToken.None))!;
        var recovery = await writer.PreserveAsync(raced, "batch-recovery", CancellationToken.None);
        h.Wire.RaceDeleteBatchPath = raced.Path;
        h.Wire.DeleteBatchAsyncJob = true;
        h.Wire.DeleteBatchPendingChecks = 1;
        var before = h.Wire.Calls.Count;
        var firstEvidence = new List<ProviderEffectCheckpoint>();
        var secondEvidence = new List<ProviderEffectCheckpoint>();
        Task first, second;
        var previous = ProviderEffectContext.Current.Value;
        try
        {
            ProviderEffectContext.Current.Value = new ProviderEffectContext(checkpoint =>
            {
                firstEvidence.Add(checkpoint);
                return Task.CompletedTask;
            });
            first = AuthorizedFixtureDeleteAsync(writer, DeleteAction(raced.Path, raced), CancellationToken.None);
            ProviderEffectContext.Current.Value = new ProviderEffectContext(checkpoint =>
            {
                secondEvidence.Add(checkpoint);
                return Task.CompletedTask;
            });
            second = AuthorizedFixtureDeleteAsync(writer, DeleteAction(deleted.Path, deleted), CancellationToken.None);
        }
        finally { ProviderEffectContext.Current.Value = previous; }
        await ExpectAsync<ExpectedTargetMismatchException>(() => first);
        await second;
        Require(firstEvidence.Select(checkpoint => checkpoint.Kind).SequenceEqual(
            new[] { "delete-submit", "delete-job", "delete-rejected" }),
            "the rejected action retains its own ordered durable submission, job and refusal evidence");
        Require(secondEvidence.Select(checkpoint => checkpoint.Kind).SequenceEqual(
            new[] { "delete-submit", "delete-job" }),
            "the successful action retains its own submission and job evidence");
        using (var firstJob = System.Text.Json.JsonDocument.Parse(firstEvidence[1].Id))
        using (var secondJob = System.Text.Json.JsonDocument.Parse(secondEvidence[1].Id))
        {
            Require(firstJob.RootElement.GetProperty("entryIndex").GetInt32() == 0 &&
                secondJob.RootElement.GetProperty("entryIndex").GetInt32() == 1 &&
                secondJob.RootElement.GetProperty("entryCount").GetInt32() == 2 &&
                firstJob.RootElement.GetProperty("jobId").GetString() ==
                    secondJob.RootElement.GetProperty("jobId").GetString(),
                "each action checkpoint identifies its own entry in the shared asynchronous job");
        }
        Require(h.Wire.LastDeleteBatchSize == 2, "concurrent deletes share one two-file batch");
        Require(h.Wire.Calls.Skip(before).Count(call => call == "/2/files/delete_batch") == 1,
            "a rejected entry is never replayed");
        Require(h.Wire.Calls.Skip(before).Count(call => call == "/2/files/delete_batch/check") == 2,
            "an asynchronous batch is polled to completion");
        Require(h.Wire.Calls.Skip(before).Count(call => call == "/2/files/get_metadata") == 1,
            "one root check; parent_rev checks each candidate atomically without redundant metadata reads");
        Require(await writer.ObserveAsync(raced.Path, CancellationToken.None) is not null,
            "a parent_rev mismatch keeps only the racing file");
        Require(await writer.ObserveAsync(deleted.Path, CancellationToken.None) is null,
            "the other batch entry succeeds despite the mismatch");
        Require(recovery.VersionId == raced.VersionId && recovery.Hash == raced.Hash,
            "batching does not replace the original recovery reference");
        await SavedDeleteJobAsync(h, raced, deleted);
        await DispatchAbsenceAsync();
        await DeleteWriteRetryAsync();
        await DeleteQueueSerializationAsync();
        await DeleteJobCancellationAsync();
        await DeleteBatchCapAsync();
        await DeleteBatchTimeoutAsync();
        await DeleteRejectionJournalWriteFailsAsync();
    }

    // A journal write that fails while recording a rejection must surface as an uncertain outcome, never a raw I/O
    // failure: the refusal may still have been applied, so the effect is not safe to replan on an I/O error.
    private static async Task DeleteRejectionJournalWriteFailsAsync()
    {
        using var h = new Harness();
        var writer = new DropboxWriter(Root(), Side.B, h.Client, Path.Combine(_root, "spool"),
            V2Settings.Default with { DeleteBatchSize = 1 });
        h.Wire.SeedFile("journal.txt", "content");
        h.Wire.DeleteWriteRefusals["journal.txt"] = 1;
        var item = (await writer.ObserveAsync("journal.txt", CancellationToken.None))!;
        var previous = ProviderEffectContext.Current.Value;
        try
        {
            ProviderEffectContext.Current.Value = new ProviderEffectContext(checkpoint =>
                checkpoint.Kind == "delete-rejected"
                    ? Task.FromException(new IOException("delete journal unavailable"))
                    : Task.CompletedTask);
            await ExpectAsync<UncertainEffectException>(() =>
                AuthorizedFixtureDeleteAsync(writer, DeleteAction("journal.txt", item), CancellationToken.None));
        }
        finally { ProviderEffectContext.Current.Value = previous; }
    }

    // The delete batch cap is a product default (now the 1000-entry provider maximum): a burst of deletes larger than
    // the cap must never send a batch larger than the cap, and must fill one batch to it.
    private static async Task DeleteBatchCapAsync()
    {
        var count = V2Settings.Default.DeleteBatchSize + 50;
        using var h = new Harness();
        // A generous max age keeps the 200 ms default from flushing a partial batch mid-arrival, which is the only way
        // this test could ever see the batch split below the cap.
        var writer = new DropboxWriter(Root(), Side.B, h.Client, Path.Combine(_root, "spool"),
            V2Settings.Default with { DeleteBatchMaxAge = TimeSpan.FromSeconds(30) });
        var items = new List<ItemState>();
        for (var index = 0; index < count; index++)
        {
            var path = $"cap-{index}.txt";
            h.Wire.SeedFile(path, "content");
            items.Add((await writer.ObserveAsync(path, CancellationToken.None))!);
        }
        var tasks = items.Select(item => AuthorizedFixtureDeleteAsync(writer, DeleteAction(item.Path, item), CancellationToken.None))
            .ToList();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(60));
        Require(h.Wire.DeleteBatchSizes.Count >= 2 &&
            h.Wire.DeleteBatchSizes.All(size => size <= V2Settings.Default.DeleteBatchSize),
            "every delete batch stays within the entry cap: " + string.Join(",", h.Wire.DeleteBatchSizes));
        Require(h.Wire.DeleteBatchSizes.Contains(V2Settings.Default.DeleteBatchSize),
            "a delete batch fills to the cap: " + string.Join(",", h.Wire.DeleteBatchSizes));
    }

    // A delete_batch launch that outstays the explicit per-batch timeout is Unknown, never replayed.
    private static async Task DeleteBatchTimeoutAsync()
    {
        using var h = new Harness();
        var writer = new DropboxWriter(Root(), Side.B, h.Client, Path.Combine(_root, "spool"),
            V2Settings.Default with { DeleteBatchSize = 2, BatchOperationTimeout = TimeSpan.FromMilliseconds(300) });
        h.Wire.SeedFile("hold-a.txt", "a");
        h.Wire.SeedFile("hold-b.txt", "b");
        var first = (await writer.ObserveAsync("hold-a.txt", CancellationToken.None))!;
        var second = (await writer.ObserveAsync("hold-b.txt", CancellationToken.None))!;
        h.Wire.DeleteBatchEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        // Never released: the launch outstays the explicit batch timeout and is cancelled by it.
        h.Wire.ReleaseDeleteBatch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstTask = AuthorizedFixtureDeleteAsync(writer, DeleteAction(first.Path, first), CancellationToken.None);
        var secondTask = AuthorizedFixtureDeleteAsync(writer, DeleteAction(second.Path, second), CancellationToken.None);
        // Wait until the batch is actually in flight before timing completion, so the 300 ms launch timeout is armed.
        await h.Wire.DeleteBatchEntered.Task;
        await ExpectAsync<UncertainEffectException>(() => firstTask.WaitAsync(TimeSpan.FromSeconds(60)));
        await ExpectAsync<UncertainEffectException>(() => secondTask.WaitAsync(TimeSpan.FromSeconds(60)));
        Require(h.Wire.Calls.Count(call => call == "/2/files/delete_batch") == 1,
            "a timed-out delete batch is never replayed");
    }

    private static async Task DeleteJobCancellationAsync()
    {
        foreach (var stopBoth in new[] { false, true })
        {
            using var h = new Harness();
            using var firstStop = new CancellationTokenSource();
            using var secondStop = new CancellationTokenSource();
            var writer = new DropboxWriter(Root(), Side.B, h.Client, Path.Combine(_root, "spool"),
                V2Settings.Default with { DeleteBatchSize = 2,
                    BatchPollInterval = stopBoth ? TimeSpan.FromHours(1) : TimeSpan.FromMilliseconds(1) });
            h.Wire.DeleteBatchAsyncJob = true;
            h.Wire.SeedFile("stop-first.txt", "first");
            h.Wire.SeedFile("stop-second.txt", "second");
            var first = (await writer.ObserveAsync("stop-first.txt", CancellationToken.None))!;
            var second = (await writer.ObserveAsync("stop-second.txt", CancellationToken.None))!;
            var evidence = new List<ProviderEffectCheckpoint>();
            Task firstTask, secondTask;
            var previous = ProviderEffectContext.Current.Value;
            try
            {
                ProviderEffectContext.Current.Value = new ProviderEffectContext(checkpoint =>
                {
                    evidence.Add(checkpoint);
                    return Task.CompletedTask;
                });
                firstTask = AuthorizedFixtureDeleteAsync(writer, DeleteAction(first.Path, first), firstStop.Token);
                ProviderEffectContext.Current.Value = new ProviderEffectContext(checkpoint =>
                {
                    if (checkpoint.Kind == "delete-job")
                    {
                        firstStop.Cancel();
                        if (stopBoth) secondStop.Cancel();
                    }
                    return Task.CompletedTask;
                });
                secondTask = AuthorizedFixtureDeleteAsync(writer, DeleteAction(second.Path, second), secondStop.Token);
            }
            finally { ProviderEffectContext.Current.Value = previous; }
            await ExpectAsync<UncertainEffectException>(() => firstTask.WaitAsync(TimeSpan.FromSeconds(2)));
            if (stopBoth)
                await ExpectAsync<UncertainEffectException>(() => secondTask.WaitAsync(TimeSpan.FromSeconds(2)));
            else await secondTask.WaitAsync(TimeSpan.FromSeconds(2));
            Require(evidence.Select(value => value.Kind).SequenceEqual(new[] { "delete-submit", "delete-job" }),
                "stopping retains the durable job identity, never records an accepted delete as refused");
            Require(h.Wire.Calls.Count(call => call == "/2/files/delete_batch") == 1,
                "stopped job entries are never replayed");
            Require(h.Wire.Calls.Count(call => call == "/2/files/delete_batch/check") == (stopBoth ? 0 : 1),
                "all stopped entries cancel polling promptly; a live sibling still receives its own result");
            using var job = System.Text.Json.JsonDocument.Parse(evidence.Single(value => value.Kind == "delete-job").Id);
            Require(await h.Client.ResolveDeleteBatchJobAsync(DropboxItems.ToEntry(first),
                job.RootElement.GetProperty("jobId").GetString()!, 0, 2, CancellationToken.None) == true,
                "a stopped entry can later reconcile the original job without resubmission");
        }
    }

    private static async Task DeleteWriteRetryAsync()
    {
        foreach (var retryLimit in new[] { 0, 1 })
        foreach (var refusals in new[] { 1, 2 })
        {
            using var h = new Harness();
            var writer = new DropboxWriter(Root(), Side.B, h.Client, Path.Combine(_root, "spool"),
                V2Settings.Default with { DeleteBatchSize = 2, DropboxWriteOperationRetryCount = retryLimit });
            h.Wire.SeedFile("retry.txt", "retry");
            h.Wire.SeedFile("success.txt", "success");
            h.Wire.DeleteWriteRefusals["retry.txt"] = refusals;
            var retry = (await writer.ObserveAsync("retry.txt", CancellationToken.None))!;
            var success = (await writer.ObserveAsync("success.txt", CancellationToken.None))!;
            var first = AuthorizedFixtureDeleteAsync(writer, DeleteAction(retry.Path, retry), CancellationToken.None);
            var second = AuthorizedFixtureDeleteAsync(writer, DeleteAction(success.Path, success), CancellationToken.None);
            if (refusals <= retryLimit) await first;
            else await ExpectAsync<ProviderWriteThrottledException>(() => first);
            await second;
            Require(h.Wire.DeleteBatchSizes.SequenceEqual(retryLimit == 0 ? new[] { 2 } : new[] { 2, 1 }),
                "only the explicit unapplied write refusal retries, at most once; a successful sibling never replays");
            Require((await writer.ObserveAsync(retry.Path, CancellationToken.None) is null) == (refusals <= retryLimit),
                "exhausted or disabled retries preserve the refused file");
        }
    }

    private static async Task DeleteQueueSerializationAsync()
    {
        using var h = new Harness();
        var settings = V2Settings.Default with { DeleteBatchSize = 2 };
        var writer = new DropboxWriter(Root(), Side.B, h.Client, Path.Combine(_root, "spool"), settings);
        var items = new List<ItemState>();
        for (var index = 0; index < 6; index++)
        {
            var path = $"queue-{index}.txt";
            h.Wire.SeedFile(path, "content");
            items.Add((await writer.ObserveAsync(path, CancellationToken.None))!);
        }
        h.Wire.DeleteBatchEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Wire.ReleaseDeleteBatch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = items.Take(2).Select(item => AuthorizedFixtureDeleteAsync(writer, DeleteAction(item.Path, item), CancellationToken.None)).ToList();
        await h.Wire.DeleteBatchEntered.Task;
        tasks.AddRange(items.Skip(2).Select(item => AuthorizedFixtureDeleteAsync(writer, DeleteAction(item.Path, item), CancellationToken.None)));
        Require(h.Wire.DeleteBatchSizes.SequenceEqual(new[] { 2 }), "arrivals accumulate behind the single in-flight delete batch");
        h.Wire.ReleaseDeleteBatch.TrySetResult();
        await Task.WhenAll(tasks);
        Require(h.Wire.DeleteBatchSizes.SequenceEqual(new[] { 2, 2, 2 }), "queued arrivals drain in full batches without concurrent launches");
    }

    private static async Task SavedDeleteJobAsync(Harness h, ItemState refused, ItemState deleted)
    {
        var before = h.Wire.Calls.Count;
        h.Wire.DeleteBatchPendingChecks = 1;
        Require(await h.Client.ResolveDeleteBatchJobAsync(DropboxItems.ToEntry(deleted), "delete-job-1", 1, 2,
            CancellationToken.None) is null, "a saved pending delete job remains unresolved");
        Require(await h.Client.ResolveDeleteBatchJobAsync(DropboxItems.ToEntry(deleted), "delete-job-1", 1, 2,
            CancellationToken.None) == true, "a saved successful entry proves its exact deleted identity");
        Require(await h.Client.ResolveDeleteBatchJobAsync(DropboxItems.ToEntry(refused), "delete-job-1", 0, 2,
            CancellationToken.None) == false, "a saved refused entry is independently not applied");
        await ExpectAsync<DropboxEffectUncertainException>(() => h.Client.ResolveDeleteBatchJobAsync(
            DropboxItems.ToEntry(deleted with { ObjectId = "id:unrelated" }), "delete-job-1", 1, 2,
            CancellationToken.None));
        await ExpectAsync<DropboxEffectUncertainException>(() => h.Client.ResolveDeleteBatchJobAsync(
            DropboxItems.ToEntry(deleted), "delete-job-1", 1, 3, CancellationToken.None));
        Require(!h.Wire.Calls.Skip(before).Contains("/2/files/delete_batch") &&
            !h.Wire.Calls.Skip(before).Contains("/2/files/delete_v2"),
            "saved pending, completed, refused and mismatched jobs are checked without replaying any delete");
    }
}
