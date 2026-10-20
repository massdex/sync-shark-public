namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    private static async Task QuotaStagingGateAsync()
    {
        using var h = new Harness();
        var local = new RootDef("ra", SyncProviders.Local, null, @"C:\synthetic", "id:local");
        var pair = new PairDef("p1", "quota-start", local, Root(), [], [], true);
        var source = new FakeEndpoint(Side.A, "ra") { DropboxHashes = true };
        var item = source.Seed("queued.bin", "queued upload");
        var action = new SyncAction("quota-start", 18, ActionKind.CopyNew, Side.B, item.Path,
            null, item, null, false, null);
        var quota = new DropboxQuota(pair, false);
        const string endpoint = "/2/files/upload_session/start";
        h.Wire.Script(endpoint, System.Net.HttpStatusCode.ServiceUnavailable, 1);
        h.Client.SessionStartDelay = (_, _) =>
        {
            quota.Suspend(); // Another in-flight upload refused while this start was backing off.
            return Task.CompletedTask;
        };
        var content = await source.OpenReadAsync(item, default);
        await using var owner = new SyncUploadSource(content, () => quota.CheckUploadStart(action));
        var previous = SyncUploadSource.Current.Value;
        SyncUploadSource.Current.Value = owner;
        try
        {
            try
            {
                await h.Writer().PutAsync(action, content, default);
                throw new InvalidOperationException("A queued staging retry ignored the quota hold.");
            }
            catch (ProviderWriteBlockedException error) when (error.Code == "insufficient_space") { }
        }
        finally { SyncUploadSource.Current.Value = previous; }
        Require(h.Wire.Calls.Count(call => call == endpoint) == 1 && h.Wire.AttemptedWritePaths.Count == 0,
            "a quota hold acquired during staging backoff prevents another byte-send attempt");

        // A large upload (over the 8 MB batching limit) must recheck the hold before its first request too:
        // the single-request path up to the provider limit, and the upload-session start path above it.
        var large = source.Seed("large.bin", new byte[9 * 1024 * 1024]);
        foreach (var useSession in new[] { false, true })
        {
            var firstRequest = useSession ? "/2/files/upload_session/start" : "/2/files/upload";
            using var largeHarness = new Harness(uploadThreshold: useSession ? 8L * 1024 * 1024 : null);
            var heldQuota = new DropboxQuota(pair, true);
            var largeAction = new SyncAction("quota-large", 18, ActionKind.CopyNew, Side.B, large.Path,
                null, large, null, false, null);
            var largeContent = await source.OpenReadAsync(large, default);
            await using var largeOwner = new SyncUploadSource(largeContent,
                () => heldQuota.CheckUploadStart(largeAction));
            var previousLarge = SyncUploadSource.Current.Value;
            SyncUploadSource.Current.Value = largeOwner;
            try
            {
                try
                {
                    await largeHarness.Writer().PutAsync(largeAction, largeContent, default);
                    throw new InvalidOperationException("A large upload ignored the quota hold.");
                }
                catch (ProviderWriteBlockedException error) when (error.Code == "insufficient_space") { }
            }
            finally { SyncUploadSource.Current.Value = previousLarge; }
            Require(largeHarness.Wire.Calls.Count(call => call == firstRequest) == 0 &&
                largeHarness.Wire.AttemptedWritePaths.Count == 0,
                "a large upload over 8 MB rechecks the quota hold before its first request");
        }
    }

    private static async Task QuotaHoldAsync(string root, bool explicitRetry)
    {
        using var h = new Harness();
        var settings = StorageChecks.Fast with
        {
            MaxWorkers = 1, ProgressInterval = TimeSpan.Zero, FailureSampleLimit = 1,
            UploadBatchSize = 1, UploadCommitConcurrency = 1, PendingUploadCommitLimit = 1
        };
        var pipelineBound = settings.MaxWorkers + settings.PendingUploadCommitLimit +
            settings.UploadBatchSize * settings.UploadCommitConcurrency;
        var uploadCount = pipelineBound + 2;
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var local = new RootDef("ra", SyncProviders.Local, null, "C:\\synthetic", "id:local");
        var pair = new PairDef("p1", "quota", local, Root(), [], [], true);
        var a = new FakeEndpoint(Side.A, "ra") { DropboxHashes = true };
        for (var i = 0; i < uploadCount; i++)
        {
            a.Seed($"upload-{i}.bin", new byte[1024 * 1024]);
            h.Wire.NameWriteErrors[$"upload-{i}.bin"] = "insufficient_space";
        }
        h.Wire.SeedFile("download.txt", "download survives quota");
        h.Wire.QuotaFull = true;
        using var store = SqliteStateStore.Open(root, settings, TimeProvider.System);
        store.SavePair(pair);
        var observedStore = new RecordingStore(store)
        {
            NoticesAdded = notices =>
            {
                if (notices.Any(n => n.UploadSuspended == true)) events.Enqueue("held");
            }
        };
        var provider = new FakeEndpointProvider().Add(local, a)
            .AddInventory(pair.B, new DropboxInventory(Root(), Side.B, h.Client, store, "p1"))
            .AddWriter(pair.B, new DropboxWriter(Root(), Side.B, h.Client, Path.Combine(root, "spool"), settings));
        // Record a start at the hold recheck itself: a passing recheck means the upload is in flight, so its
        // start must order before the refusal observation and its persisted notice (not at the fake wire).
        var previousBeforeUpload = SyncUploadSource.BeforeUploadObserved;
        SyncUploadSource.BeforeUploadObserved = () => events.Enqueue("start");
        var run = new SyncRun(store, new Executor(observedStore, settings, ExecutorChecks.Admission()), provider, settings, TimeProvider.System);
        var plan = await run.PreviewFirstSyncAsync("p1", new RecordingProgress(), default);
        var report = await run.ApplyAsync("p1", plan, new RecordingProgress(), default);
        Require(report.Downloaded == 1 && await a.ObserveAsync("download.txt", default) is not null,
            "quota suspends only uploads; download completes in the same run");
        var firstEvents = events.ToArray();
        var heldAt = Array.IndexOf(firstEvents, "held");
        Require(heldAt >= 0 && !firstEvents.Skip(heldAt + 1).Contains("start"),
            "no upload starts after the executor observes and persists the first quota refusal");
        var attempted = h.Wire.AttemptedWritePaths.Count;
        var starts = firstEvents.Count(e => e == "start");
        Require(attempted >= 1 && attempted <= pipelineBound && starts == attempted && report.Failed == uploadCount,
            "only bounded in-flight uploads reach Dropbox; all quota items remain failed retries");
        Require(store.ReadSyncRetries("p1").Count(r => r.Path.StartsWith("upload-", StringComparison.Ordinal)) == uploadCount &&
            store.Baselines("p1").All(b => !b.A.Path.StartsWith("upload-", StringComparison.Ordinal)),
            "all quota items retain retry debt and never gain a synced baseline");
        Require(store.OpenNotices("p1").Count(n => n.ReasonKey == DropboxQuota.NoticeKey) == 1 &&
            !store.OpenNotices("p1").Any(n => n.ReasonKey == "sync-retry"),
            "one quota notice even beyond the failure sample limit");
        var cursor = store.Cursor(pair.B.RootId)!;
        h.Wire.SeedChangePage(cursor, [], false, cursor + "-idle");
        await run.IncrementalAsync("p1", new RecordingProgress(), default);
        Require(h.Wire.AttemptedWritePaths.Count == attempted && events.Count(e => e == "start") == starts,
            "full quota never retransfers a large file on the next run");
        var notice = store.OpenNotices("p1").Single(n => n.ReasonKey == DropboxQuota.NoticeKey);
        h.Wire.QuotaFull = false;
        h.Wire.QuotaFreeBytes = 100;
        var probes = h.Wire.Calls.Count(call => call == "/2/users/get_space_usage");
        await run.IncrementalAsync("p1", new RecordingProgress(), default);
        Require(h.Wire.Calls.Count(call => call == "/2/users/get_space_usage") == probes + 1 &&
            h.Wire.AttemptedWritePaths.Count == attempted && events.Count(e => e == "start") == starts &&
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
        Require(resumed.Uploaded == uploadCount && h.Wire.AttemptedWritePaths.Count == attempted + uploadCount,
            "explicit Retry or sufficient-space probe releases all retry debt without manual timestamp resets");
        Require(store.NoticeById("p1", notice.NoticeId!)?.Notice.UploadSuspended == false,
            "quota release is durable, including after Ignore");
        SyncUploadSource.BeforeUploadObserved = previousBeforeUpload;
        Console.WriteLine("PASS quota: one notice, upload-only suspension, durable retries, no retransfers, downloads and explicit Retry");
    }
}
