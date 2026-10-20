namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    private static async Task QuotaHoldAsync(string root, bool explicitRetry)
    {
        using var h = new Harness();
        var settings = StorageChecks.Fast with { MaxWorkers = 1, ProgressInterval = TimeSpan.Zero, FailureSampleLimit = 1 };
        var local = new RootDef("ra", SyncProviders.Local, null, "C:\\synthetic", "id:local");
        var pair = new PairDef("p1", "quota", local, Root(), [], [], true);
        var a = new FakeEndpoint(Side.A, "ra") { DropboxHashes = true };
        for (var i = 0; i < 3; i++)
        {
            a.Seed($"upload-{i}.bin", new byte[1024 * 1024]);
            h.Wire.NameWriteErrors[$"upload-{i}.bin"] = "insufficient_space";
        }
        h.Wire.SeedFile("download.txt", "download survives quota");
        h.Wire.QuotaFull = true;
        using var store = SqliteStateStore.Open(root, settings, TimeProvider.System);
        store.SavePair(pair);
        var provider = new FakeEndpointProvider().Add(local, a)
            .AddInventory(pair.B, new DropboxInventory(Root(), Side.B, h.Client, store, "p1"))
            .AddWriter(pair.B, h.Writer());
        var run = new SyncRun(store, new Executor(store, settings, ExecutorChecks.Admission()), provider, settings, TimeProvider.System);
        var plan = await run.PreviewFirstSyncAsync("p1", new RecordingProgress(), default);
        var report = await run.ApplyAsync("p1", plan, new RecordingProgress(), default);
        Require(report.Downloaded == 1 && await a.ObserveAsync("download.txt", default) is not null,
            "quota suspends only uploads; download completes in the same run");
        Require(h.Wire.AttemptedWritePaths.Count == 1 && report.Failed == 3,
            "first quota failure suspends remaining uploads without sending their bytes");
        Require(store.ReadSyncRetries("p1").Count(r => r.Path.StartsWith("upload-", StringComparison.Ordinal)) == 3 &&
            store.Baselines("p1").All(b => !b.A.Path.StartsWith("upload-", StringComparison.Ordinal)),
            "all quota items retain retry debt and never gain a synced baseline");
        Require(store.OpenNotices("p1").Count(n => n.ReasonKey == DropboxQuota.NoticeKey) == 1 &&
            !store.OpenNotices("p1").Any(n => n.ReasonKey == "sync-retry"),
            "one quota notice even beyond the failure sample limit");
        var cursor = store.Cursor(pair.B.RootId)!;
        h.Wire.SeedChangePage(cursor, [], false, cursor + "-idle");
        await run.IncrementalAsync("p1", new RecordingProgress(), default);
        Require(h.Wire.AttemptedWritePaths.Count == 1, "full quota never retransfers a large file on the next run");
        var notice = store.OpenNotices("p1").Single(n => n.ReasonKey == DropboxQuota.NoticeKey);
        h.Wire.QuotaFull = false;
        h.Wire.QuotaFreeBytes = 100;
        var probes = h.Wire.Calls.Count(call => call == "/2/users/get_space_usage");
        await run.IncrementalAsync("p1", new RecordingProgress(), default);
        Require(h.Wire.Calls.Count(call => call == "/2/users/get_space_usage") == probes + 1 &&
            h.Wire.AttemptedWritePaths.Count == 1 &&
            store.NoticeById("p1", notice.NoticeId!) is { Closed: false, Notice.UploadSuspended: true },
            "real quota recheck keeps a 1 MB upload held with only 100 bytes free, without closing its notice");
        foreach (var path in h.Wire.NameWriteErrors.Keys.ToArray()) h.Wire.NameWriteErrors.Remove(path);
        if (explicitRetry)
            await run.ApplyRetryNoticeChoiceAsync("p1", notice.NoticeId!, NoticeChoices.RetryNow, default);
        else
        {
            await run.ApplyRetryNoticeChoiceAsync("p1", notice.NoticeId!, NoticeChoices.Ignore, default);
            h.Wire.QuotaFreeBytes = 10 * 1024 * 1024;
        }
        var resumed = await run.IncrementalAsync("p1", new RecordingProgress(), default);
        Require(resumed.Uploaded == 3 && h.Wire.AttemptedWritePaths.Count == 4,
            "explicit Retry or sufficient-space probe releases all retry debt without manual timestamp resets");
        Require(store.NoticeById("p1", notice.NoticeId!)?.Notice.UploadSuspended == false,
            "quota release is durable, including after Ignore");
        Console.WriteLine("PASS quota: one notice, upload-only suspension, durable retries, no retransfers, downloads and explicit Retry");
    }
}
