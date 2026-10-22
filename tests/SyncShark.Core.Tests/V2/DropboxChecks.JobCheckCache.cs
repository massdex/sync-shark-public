using System.Text.Json;

namespace Massdex.SyncShark.V2;

// One asynchronous Dropbox job (create_folder_batch or delete_batch) backs up to 1,000 intents, and every intent
// carries the same job id in its checkpoint. Reconcile used to check the job once per intent; one pass now checks
// each distinct job id once and shares the read, while every entry still interprets its own result.
internal static partial class DropboxChecks
{
    private const string MkDirJobCheck = "/2/files/create_folder_batch/check";
    private const string DeleteJobCheck = "/2/files/delete_batch/check";

    private static async Task JobCheckCacheChecksAsync()
    {
        await MkDirJobCheckedOncePerPassAsync();
        await DeleteJobCheckedOncePerPassAsync();
        await JobCheckCacheDropsFailureAsync();
    }

    private static async Task MkDirJobCheckedOncePerPassAsync()
    {
        const int count = 8;
        using var h = new Harness();
        var writer = new DropboxWriter(Root(), Side.B, h.Client, Path.Combine(_root, "spool"),
            V2Settings.Default with { MkDirBatchSize = count, MkDirBatchMaxAge = MkDirFlushGrace });
        var created = await Task.WhenAll(Enumerable.Range(0, count)
                .Select(index => writer.MkDirAsync("job-once-" + index, CancellationToken.None)))
            .WaitAsync(TimeSpan.FromSeconds(30));
        Require(h.Wire.Calls.Count(call => call == MkDirJobCheck) == 0,
            "a completed synchronous create batch needs no job check");

        // Eight intents naming one job: one provider check for the pass, and every entry still resolves itself.
        var before = h.Wire.Calls.Count;
        var results = await ReconcileCheckedAsync(h, writer, created
            .Select((item, index) => MkDirJobIntent(item.Path, "mkdir-job-1", index, count)).ToArray());
        Require(h.Wire.Calls.Skip(before).Count(call => call == MkDirJobCheck) == 1,
            "a create job shared by eight intents is checked once per reconcile pass, not once per intent: " +
            string.Join(",", h.Wire.Calls.Skip(before)));
        Require(results.All(result => result.Outcome == Outcome.Done),
            "every intent still reads its own job entry and resolves Done");

        // Two distinct job ids are two distinct checks.
        var splitBefore = h.Wire.Calls.Count;
        results = await ReconcileCheckedAsync(h, writer, created
            .Select((item, index) => MkDirJobIntent(item.Path,
                index % 2 == 0 ? "mkdir-job-1" : "mkdir-job-2", index, count)).ToArray());
        Require(h.Wire.Calls.Skip(splitBefore).Count(call => call == MkDirJobCheck) == 2,
            "two distinct create job ids cost two checks in one pass: " +
            string.Join(",", h.Wire.Calls.Skip(splitBefore)));
        Require(results.All(result => result.Outcome == Outcome.Done),
            "both create jobs' entries resolve Done");
    }

    private static async Task DeleteJobCheckedOncePerPassAsync()
    {
        const int count = 6;
        using var h = new Harness();
        var writer = new DropboxWriter(Root(), Side.B, h.Client, Path.Combine(_root, "spool"),
            V2Settings.Default with { DeleteBatchSize = count });
        var items = new List<ItemState>(count);
        for (var index = 0; index < count; index++)
        {
            var path = "job-del-" + index + ".txt";
            h.Wire.SeedFile(path, "content");
            items.Add((await writer.ObserveAsync(path, CancellationToken.None))!);
        }
        var source = new FakeEndpoint(Side.A, "ra") { DropboxHashes = true };
        h.Known.SavePair(new PairDef("p1", "delete job cache",
            new RootDef("ra", SyncProviders.Local, null, "C:\\offline-job-cache", "id:ra"), Root(), [], [], true));
        var intents = new List<SyncAction>(count);
        foreach (var (item, index) in items.Select((item, index) => (item, index)))
        {
            var original = source.Seed(item.Path, "content");
            var key = "delete-key-" + index;
            var adopt = new SyncAction("seed-" + index, 1, ActionKind.Adopt, Side.B,
                item.Path, null, original, item, false, key);
            h.Known.RecordIntent("p1", adopt);
            h.Known.CommitResult("p1", 1, adopt,
                new ActionResult(adopt.ActionId, Outcome.Done, original, item, null, null));
            await source.DeleteAsync(DeleteAction(original.Path, original) with { Target = Side.A }, CancellationToken.None);
            var intent = DeleteJobIntent(item, "delete-job-1", index, count) with { ObjectKey = key };
            h.Known.RecordIntent("p1", intent);
            intents.Add(intent);
        }
        await Task.WhenAll(items
                .Select(item => writer.DeleteAsync(DeleteAction(item.Path, item), CancellationToken.None)))
            .WaitAsync(TimeSpan.FromSeconds(30));
        Require(h.Wire.Calls.Count(call => call == DeleteJobCheck) == 0,
            "a completed synchronous delete batch needs no job check");
        h.Wire.DeleteBatchPendingChecks = 2;
        var before = h.Wire.Calls.Count;
        var results = await ReconcileCheckedAsync(h, writer, intents, source);
        Require(h.Wire.Calls.Skip(before).Count(call => call == DeleteJobCheck) == 3,
            "one shared delete job poll handles two pending checks then completion, not one poll per intent: " +
            string.Join(",", h.Wire.Calls.Skip(before)));
        Require(results.Count == count && results.All(result => result.Outcome == Outcome.Done),
            "every delete intent still proves its own entry Done");
        Require(h.Known.Baselines("p1").Count == 0 && h.Known.UnresolvedIntents("p1").Count == 0,
            "proven delete entries retire their original baselines and intents");
    }

    // A failed job read is dropped from the pass cache, so the next entry of that job reads it again rather than
    // inheriting a failure that was never a job outcome.
    private static async Task JobCheckCacheDropsFailureAsync()
    {
        var cache = new ProviderJobCheckCache();
        var owner = new object();
        var reads = 0;
        using var document = JsonDocument.Parse("{\"status\":\"complete\"}");
        var status = document.RootElement.Clone();
        async Task<JsonElement> ReadAsync(CancellationToken ct)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref reads) == 1) throw new IOException("transient job check failure");
            return status;
        }

        await ExpectAsync<IOException>(() =>
            cache.ReadAsync(owner, "mkdir", "job-1", ReadAsync, CancellationToken.None));
        var second = await cache.ReadAsync(owner, "mkdir", "job-1", ReadAsync, CancellationToken.None);
        Require(reads == 2, "a failed job read is not cached: the next entry reads the job again");
        Require(second.GetProperty("status").GetString() == "complete",
            "the retried read returns the job status");

        // Two consecutive failures: one retry, then the failure is shared instead of being retried per entry.
        var otherOwner = new object();
        var failingReads = 0;
        async Task<JsonElement> AlwaysFailAsync(CancellationToken ct)
        {
            await Task.Yield();
            Interlocked.Increment(ref failingReads);
            throw new IOException("job unavailable");
        }
        await ExpectAsync<IOException>(() =>
            cache.ReadAsync(otherOwner, "mkdir", "job-2", AlwaysFailAsync, CancellationToken.None));
        await ExpectAsync<IOException>(() =>
            cache.ReadAsync(otherOwner, "mkdir", "job-2", AlwaysFailAsync, CancellationToken.None));
        await ExpectAsync<IOException>(() =>
            cache.ReadAsync(otherOwner, "mkdir", "job-2", AlwaysFailAsync, CancellationToken.None));
        Require(failingReads == 2,
            "an unavailable job is retried once, then its failure is shared for the rest of the pass: " + failingReads);
    }

    private static async Task<IReadOnlyList<ActionResult>> ReconcileCheckedAsync(Harness h, DropboxWriter writer,
        IReadOnlyList<SyncAction> intents, IEndpointWriter? source = null)
    {
        var executor = new Executor(h.Known, V2Settings.Default, ExecutorChecks.Admission());
        return await executor.ReconcileAsync(intents, 1, source ?? writer, writer, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(60));
    }

    private static SyncAction MkDirJobIntent(string path, string jobId, int entryIndex, int entryCount) =>
        new("job-mkdir-" + jobId + "-" + entryIndex, 4, ActionKind.MkDir, Side.B, path, null, null, null, false,
            null)
        {
            ProviderEffect = new ProviderEffectCheckpoint("mkdir-job",
                JsonSerializer.Serialize(new { jobId, entryIndex, entryCount }), DateTimeOffset.UtcNow),
        };

    private static SyncAction DeleteJobIntent(ItemState expected, string jobId, int entryIndex, int entryCount) =>
        new("job-delete-" + jobId + "-" + entryIndex, 6, ActionKind.Delete, Side.B, expected.Path, null, null,
            expected, false, null)
        {
            ProviderEffect = new ProviderEffectCheckpoint("delete-job",
                JsonSerializer.Serialize(new { jobId, entryIndex, entryCount }), DateTimeOffset.UtcNow),
        };
}
