using System.Text.Json;

namespace Massdex.SyncShark.V2;

// The folder-create batch contract: coalesced creates with no per-folder ancestor walk, an existing folder as
// success, a per-entry refusal owning only its own action, and an asynchronous job polled to completion.
internal static partial class DropboxChecks
{
    // A generous flush age keeps a partial batch from draining mid-arrival, so only the entry cap splits a burst.
    private static readonly TimeSpan MkDirFlushGrace = TimeSpan.FromSeconds(5);

    private static async Task MkDirBatchChecksAsync()
    {
        await MkDirBatchCoalescingAsync();
        await MkDirRealDropboxSuccessShapeAsync();
        await MkDirExistingFolderAsync();
        await MkDirEntryFailureAsync();
        await MkDirAsyncJobAsync();
        await MkDirWriteRetryAsync();
        await MkDirNestedEntryShapeAsync();
        await ParentEnsureIsOneCheckAsync();
    }

    // Dropbox's real create-batch success entry nests an UNTAGGED FolderMetadata (a struct, not the Metadata union:
    // no ".tag" inside), while the root's display case can differ from the saved pair root. Batches of 1,000 also run
    // asynchronously, so the same entries arrive through create_folder_batch/check. Run with the ambient account
    // verification scope v2 runs now hold, over several batches.
    private static async Task MkDirRealDropboxSuccessShapeAsync()
    {
        using var h = new Harness();
        // Dropbox's displayed casing differs from the saved pair root; every path comparison must stay case-insensitive.
        h.Wire.RootPath = "/pilot";
        h.Wire.MkDirSuccessUntaggedMetadata = true;
        var writer = new DropboxWriter(Root(), Side.B, h.Client, Path.Combine(_root, "spool"), isStateDamage: StorageFailureClassifier.IsStateDamage,
            V2Settings.Default with { MkDirBatchSize = 9, MkDirBatchMaxAge = MkDirFlushGrace });
        await using var scope = SyncProviderAccountVerificationScope.Begin(CancellationToken.None);

        // One synchronous completion batch.
        var sync = writer.MkDirAsync("shape-sync", CancellationToken.None);
        var syncSibling = writer.MkDirAsync("shape-sync-2", CancellationToken.None);
        var created = await Task.WhenAll(sync, syncSibling).WaitAsync(TimeSpan.FromSeconds(30));
        Require(created.All(item => item.Kind == ItemKind.Folder && item.ObjectId.Length != 0 &&
                item.Path is "shape-sync" or "shape-sync-2"),
            "an untagged FolderMetadata success entry is read as the created folder");

        // The same shape through an asynchronous job check, in two more batches.
        h.Wire.MkDirBatchAsyncJob = true;
        h.Wire.MkDirBatchPendingChecks = 1;
        var firstWave = await Task.WhenAll(Enumerable.Range(0, 9).Select(index =>
            writer.MkDirAsync("job-" + index.ToString("D2"), CancellationToken.None))).WaitAsync(TimeSpan.FromSeconds(60));
        h.Wire.MkDirBatchPendingChecks = 1;
        var secondWave = await Task.WhenAll(Enumerable.Range(0, 3).Select(index =>
            writer.MkDirAsync("tail-" + index.ToString("D2"), CancellationToken.None))).WaitAsync(TimeSpan.FromSeconds(60));
        Require(firstWave.Concat(secondWave).All(item => item.Kind == ItemKind.Folder && item.ObjectId.Length != 0),
            "an asynchronous create job's untagged FolderMetadata entries are read as created folders");
        Require(h.Wire.Calls.Count(call => call == "/2/files/create_folder_batch") >= 3 &&
            h.Wire.Calls.Count(call => call == "/2/files/create_folder_batch/check") >= 2,
            "the realistic shape is exercised through both the synchronous and the polled path");
    }

    // 2,500 folder creates under existing parents: three create_folder_batch calls and no per-folder ancestor walk.
    private static async Task MkDirBatchCoalescingAsync()
    {
        using var h = new Harness();
        for (var parent = 0; parent < 50; parent++) h.Wire.SeedFolder("p" + parent.ToString("D2"));
        var writer = new DropboxWriter(Root(), Side.B, h.Client, Path.Combine(_root, "spool"), isStateDamage: StorageFailureClassifier.IsStateDamage,
            V2Settings.Default with { MkDirBatchSize = 1000, MkDirBatchMaxAge = MkDirFlushGrace });
        // Hold the first batch in flight so every create is queued before any batch is drained: the entry cap,
        // never the arrival timing, decides every batch's size.
        h.Wire.MkDirBatchEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Wire.ReleaseMkDirBatch = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var metadataBefore = h.Wire.MetadataPaths.Count;
        var tasks = new List<Task<ItemState>>(2500);
        for (var parent = 0; parent < 50; parent++)
        for (var child = 0; child < 50; child++)
            tasks.Add(writer.MkDirAsync("p" + parent.ToString("D2") + "/c" + child.ToString("D3"),
                CancellationToken.None));
        await h.Wire.MkDirBatchEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
        h.Wire.ReleaseMkDirBatch.TrySetResult();
        var created = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(120));
        Require(h.Wire.MkDirBatchSizes.SequenceEqual(new[] { 1000, 1000, 500 }),
            "2,500 creates coalesce into three capped calls: " + string.Join(",", h.Wire.MkDirBatchSizes));
        var reads = h.Wire.MetadataPaths.Skip(metadataBefore).ToArray();
        // One verified root read before the commit and one cheap re-read after it, per batch; never an ancestor.
        Require(reads.Length == h.Wire.MkDirBatchSizes.Count * 2 &&
            reads.All(path => path == DropboxWire.RootId || path == "/Pilot"),
            "a batched create reads only each batch's own root identity before and after the commit, never an ancestor: " + string.Join(",", reads));
        Require(created.Length == 2500 && created.All(item => item.Kind == ItemKind.Folder) &&
            created.All(item => item.ObjectId.Length != 0), "every created folder carries its own id");
        Require(created.Select(item => item.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2500,
            "every planned path is reported exactly once");
    }

    // An existing folder is the create's own result: the folder id is read back, never a conflict.
    private static async Task MkDirExistingFolderAsync()
    {
        using var h = new Harness();
        var writer = h.Writer();
        h.Wire.SeedFolder("existing");
        var existing = (await writer.ObserveAsync("existing", CancellationToken.None))!;
        var created = await writer.MkDirAsync("existing", CancellationToken.None);
        Require(created.Kind == ItemKind.Folder && created.ObjectId == existing.ObjectId,
            "an already-existing folder counts as the create's own success, with its id read back");
        Require(h.Wire.Calls.Contains("/2/files/create_folder_batch") &&
            !h.Wire.Calls.Contains("/2/files/create_folder_v2"), "the create goes through the batch endpoint");
        var fresh = await writer.MkDirAsync("fresh", CancellationToken.None);
        Require(fresh.Kind == ItemKind.Folder && fresh.ObjectId.Length != 0 && fresh.ObjectId != existing.ObjectId,
            "a fresh folder carries its own created id");
    }

    // A per-entry refusal is that entry's own failure; its batch sibling is unaffected.
    private static async Task MkDirEntryFailureAsync()
    {
        using var h = new Harness();
        var writer = new DropboxWriter(Root(), Side.B, h.Client, Path.Combine(_root, "spool"), isStateDamage: StorageFailureClassifier.IsStateDamage,
            V2Settings.Default with { MkDirBatchSize = 2, MkDirBatchMaxAge = MkDirFlushGrace });
        h.Wire.SeedFile("taken.txt", "occupied");
        var good = writer.MkDirAsync("free", CancellationToken.None);
        var bad = writer.MkDirAsync("taken.txt", CancellationToken.None);
        Require((await good).Kind == ItemKind.Folder, "the sibling create succeeds despite the refused entry");
        await ExpectAsync<ExpectedTargetMismatchException>(() => bad);
        Require(await writer.ObserveAsync("free", CancellationToken.None) is { Kind: ItemKind.Folder },
            "the good folder exists");
        Require(await writer.ObserveAsync("taken.txt", CancellationToken.None) is { Kind: ItemKind.File },
            "the occupied path is left untouched");
        Require(h.Wire.MkDirBatchSizes.SequenceEqual(new[] { 2 }), "one batch carried both entries");
    }

    private static async Task MkDirAsyncJobAsync()
    {
        using var h = new Harness();
        var writer = new DropboxWriter(Root(), Side.B, h.Client, Path.Combine(_root, "spool"), isStateDamage: StorageFailureClassifier.IsStateDamage,
            V2Settings.Default with { MkDirBatchSize = 2, MkDirBatchMaxAge = MkDirFlushGrace });
        h.Wire.MkDirBatchAsyncJob = true;
        h.Wire.MkDirBatchPendingChecks = 1;
        var first = new List<ProviderEffectCheckpoint>();
        var second = new List<ProviderEffectCheckpoint>();
        Task<ItemState> a, b;
        var previous = ProviderEffectContext.Current.Value;
        try
        {
            ProviderEffectContext.Current.Value = new ProviderEffectContext(checkpoint =>
            {
                first.Add(checkpoint);
                return Task.CompletedTask;
            });
            a = writer.MkDirAsync("job-a", CancellationToken.None);
            ProviderEffectContext.Current.Value = new ProviderEffectContext(checkpoint =>
            {
                second.Add(checkpoint);
                return Task.CompletedTask;
            });
            b = writer.MkDirAsync("job-b", CancellationToken.None);
        }
        finally { ProviderEffectContext.Current.Value = previous; }
        var items = await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(60));
        Require(h.Wire.Calls.Count(call => call == "/2/files/create_folder_batch") == 1,
            "both creates share one batch");
        Require(h.Wire.Calls.Count(call => call == "/2/files/create_folder_batch/check") == 2,
            "an asynchronous create job is polled to completion");
        Require(first.Select(checkpoint => checkpoint.Kind).SequenceEqual(new[] { "mkdir-submit", "mkdir-job" }) &&
            second.Select(checkpoint => checkpoint.Kind).SequenceEqual(new[] { "mkdir-submit", "mkdir-job" }),
            "each action's submission and job identity are durable before polling");
        using (var job = JsonDocument.Parse(first[1].Id))
        {
            Require(job.RootElement.GetProperty("entryIndex").GetInt32() == 0 &&
                job.RootElement.GetProperty("entryCount").GetInt32() == 2 &&
                job.RootElement.GetProperty("jobId").GetString() == "mkdir-job-1",
                "the checkpoint identifies this action's entry inside the shared job");
        }
        Require(items.All(item => item.Kind == ItemKind.Folder), "both folders exist after the job completes");
        h.Wire.MkDirBatchPendingChecks = 1;
        Require(await h.Client.ResolveMkDirBatchJobAsync("job-a", "mkdir-job-1", 0, 2, CancellationToken.None) is null,
            "a saved job still in progress stays unresolved");
        Require(await h.Client.ResolveMkDirBatchJobAsync("job-a", "mkdir-job-1", 0, 2, CancellationToken.None) == true,
            "a saved completed job proves the folder exists");
        await ExpectAsync<DropboxEffectUncertainException>(() =>
            h.Client.ResolveMkDirBatchJobAsync("job-a", "mkdir-job-1", 0, 3, CancellationToken.None));
    }

    // Only the explicit, guaranteed-not-applied write refusal retries, at most once; a sibling never replays.
    private static async Task MkDirWriteRetryAsync()
    {
        foreach (var retryLimit in new[] { 0, 1 })
        foreach (var refusals in new[] { 1, 2 })
        {
            using var h = new Harness();
            var writer = new DropboxWriter(Root(), Side.B, h.Client, Path.Combine(_root, "spool"), isStateDamage: StorageFailureClassifier.IsStateDamage,
                V2Settings.Default with
                {
                    MkDirBatchSize = 2, MkDirBatchMaxAge = MkDirFlushGrace,
                    DropboxWriteOperationRetryCount = retryLimit,
                });
            h.Wire.MkDirWriteRefusals["retrydir"] = refusals;
            var retry = writer.MkDirAsync("retrydir", CancellationToken.None);
            var sibling = writer.MkDirAsync("successdir", CancellationToken.None);
            if (refusals <= retryLimit) await retry;
            else await ExpectAsync<ProviderWriteThrottledException>(() => retry);
            await sibling;
            Require(h.Wire.MkDirBatchSizes.SequenceEqual(retryLimit == 0 ? new[] { 2 } : new[] { 2, 1 }),
                "only the explicit unapplied write refusal retries, at most once; the sibling never replays");
            Require((await writer.ObserveAsync("retrydir", CancellationToken.None) is not null) == (refusals <= retryLimit),
                "exhausted or disabled retries leave the refused folder absent");
        }
    }

    // Dropbox's own union spelling, {"success":{"metadata":{...}}}, is accepted (as the shared batch reader does).
    private static async Task MkDirNestedEntryShapeAsync()
    {
        using var h = new Harness();
        var writer = h.Writer();
        h.Wire.MkDirBatchNestedShape = true;
        var created = await writer.MkDirAsync("nested", CancellationToken.None);
        Require(created.Kind == ItemKind.Folder && created.ObjectId.Length != 0,
            "a nested success entry is read as a create");
    }

    // The large-upload and move paths still ensure parents, but with one nearest-ancestor read, not one per level.
    private static async Task ParentEnsureIsOneCheckAsync()
    {
        using var h = new Harness();
        var writer = new DropboxWriter(Root(), Side.B, h.Client, Path.Combine(_root, "spool"), isStateDamage: StorageFailureClassifier.IsStateDamage,
            V2Settings.Default with { UploadBatchSize = 1, DeleteBatchSize = 1, MkDirBatchSize = 1 });
        foreach (var folder in new[] { "m", "m/a", "m/a/b", "m/a/b/c" }) h.Wire.SeedFolder(folder);
        var bytes = new byte[(8 * 1024 * 1024) + 1];

        var shallowBefore = h.Wire.MetadataPaths.Count;
        await writer.PutAsync(PutAction("big.bin", Source("big.bin", bytes)), new MemoryStream(bytes),
            CancellationToken.None);
        var shallowUpload = h.Wire.MetadataPaths.Count - shallowBefore;
        Require(shallowUpload == 0, "a top-level large upload probes no parent: " + shallowUpload);

        var deepBefore = h.Wire.MetadataPaths.Count;
        await writer.PutAsync(PutAction("m/a/b/c/deep.bin", Source("deep.bin", bytes)), new MemoryStream(bytes),
            CancellationToken.None);
        var deepUpload = h.Wire.MetadataPaths.Count - deepBefore;
        Require(deepUpload == 1 && h.Wire.MetadataPaths[deepBefore] == DropboxWire.RootId + "/m/a/b/c",
            "a large upload probes its nearest parent once, not every level: " + deepUpload);

        h.Wire.SeedFile("shallow-src.txt", "content");
        h.Wire.SeedFile("deep-src.txt", "content");
        var shallowSource = (await writer.ObserveAsync("shallow-src.txt", CancellationToken.None))!;
        var shallowMoveBefore = h.Wire.MetadataPaths.Count;
        await writer.MoveAsync(MoveAction("moved-shallow.txt", "shallow-src.txt", shallowSource),
            CancellationToken.None);
        var shallowMove = h.Wire.MetadataPaths.Count - shallowMoveBefore;

        var deepSource = (await writer.ObserveAsync("deep-src.txt", CancellationToken.None))!;
        var deepMoveBefore = h.Wire.MetadataPaths.Count;
        await writer.MoveAsync(MoveAction("m/a/b/c/moved-deep.txt", "deep-src.txt", deepSource),
            CancellationToken.None);
        var deepMove = h.Wire.MetadataPaths.Count - deepMoveBefore;
        Require(deepMove == shallowMove + 1,
            $"a deep move probes one extra parent level, not every level: shallow={shallowMove} deep={deepMove}");
    }
}
