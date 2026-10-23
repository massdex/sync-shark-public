using Massdex.SyncShark;
using Massdex.SyncShark.V2;
using Microsoft.Data.Sqlite;

internal static class StorageExplicitRetryChecks
{
    private enum Queue { Settlement, Offline, Refresh }
    private static readonly TimeSpan Deferred = TimeSpan.FromHours(1);

    internal static async Task RunAsync(string root)
    {
        foreach (var queue in Enum.GetValues<Queue>())
        {
            var directory = Path.Combine(root, queue.ToString());
            await ActiveAsync(Path.Combine(directory, "active"), queue);
            await SuspendedAsync(Path.Combine(directory, "paused"), queue, disabled: false);
            await SuspendedAsync(Path.Combine(directory, "disabled"), queue, disabled: true);
            await PausedDispatcherAsync(Path.Combine(directory, "paused-dispatcher"), queue);
            await AtomicRetryAsync(Path.Combine(directory, "atomic"), queue);
            await FailedAsync(Path.Combine(directory, "failed"), queue);
            await ConcurrentAsync(Path.Combine(directory, "concurrent"), queue);
            foreach (var cause in new[] { "network", "auth", "provider" })
            {
                await PairCauseAsync(Path.Combine(directory, "recovered-" + cause), queue, cause, succeeds: true, clearsPair: true);
                await PairCauseAsync(Path.Combine(directory, "failed-" + cause), queue, cause, succeeds: false, clearsPair: false);
            }
            foreach (var cause in new[] { "binding", "volume", "host", "backlog", "unknown" })
                await PairCauseAsync(Path.Combine(directory, "preserved-" + cause), queue, cause, succeeds: true, clearsPair: false);
            Priority(Path.Combine(directory, "priority"), queue);
            FailurePolicyReopen(Path.Combine(directory, "failure-policy"), queue);
        }
        foreach (var cause in new[] { "network", "auth", "provider" })
            await CliPairNoticeAsync(Path.Combine(root, "cli-pair-" + cause), cause, active: true, clears: true);
        foreach (var cause in new[] { "volume", "unknown" })
            await CliPairNoticeAsync(Path.Combine(root, "cli-pair-kept-" + cause), cause, active: true, clears: false);
        await CliPairNoticeAsync(Path.Combine(root, "cli-pair-paused"), "network", active: false, clears: false);
        await CliPausedItemAsync(Path.Combine(root, "cli-paused-item"));
        Console.WriteLine("PASS explicit Retry across settlement/offline/refresh: exact target, pause/disable persistence, re-deferral, shared-gate idempotence and cursor-independent priority (synthetic providers only)");
        Console.WriteLine("PASS direct CLI Retry: pair backoff clears only for connectivity causes on an active pair, and a paused pair keeps it with the queued explanation on the notice");
    }

    private static void FailurePolicyReopen(string root, Queue queue)
    {
        string rootId, objectId;
        long expectedDue;
        using (var fixture = new Fixture(root, queue))
        {
            rootId = fixture.Pair.A.RootId; objectId = fixture.Files[0].ObjectId;
            for (var n = 0; n < 2; n++)
            {
                switch (queue)
                {
                    case Queue.Settlement: fixture.Store.FailStorageSettlement(fixture.Settlement(0)); break;
                    case Queue.Offline: fixture.Store.FailStorageHydration(fixture.Hydration(0)); break;
                    default: fixture.Store.FailStoragePointer(fixture.Pointer(0)); break;
                }
            }
            expectedDue = fixture.Clock.NowMs + 20_000;
            Require(fixture.Due(0) == expectedDue, queue + ": shared owner applies second-attempt twenty-second delay");
        }
        using var reopened = SqliteStateStore.Open(root, V2Settings.Default, new RetryClock());
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = SqliteStateStore.DatabasePath(root), Mode = SqliteOpenMode.ReadOnly }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        var table = queue switch { Queue.Settlement => "storage_settlement", Queue.Offline => "storage_hydration", _ => "storage_refresh" };
        command.CommandText = $"SELECT failure_attempts,due FROM {table} WHERE root_id=$r AND object_id=$o;";
        command.Parameters.AddWithValue("$r", rootId); command.Parameters.AddWithValue("$o", objectId);
        using var reader = command.ExecuteReader();
        Require(reader.Read() && reader.GetInt64(0) == 2 && reader.GetInt64(1) == expectedDue,
            queue + ": entity-owned attempts and deadline survive cold reopen");
    }

    /// <summary>A direct CLI pair-notice Retry never performs native work; it clears the root backoff only for
    /// connectivity evidence on an active pair, leaving every other cause and a paused pair backed off.</summary>
    private static async Task CliPairNoticeAsync(string root, string cause, bool active, bool clears)
    {
        using var fixture = new Fixture(root, Queue.Settlement);
        StorageNotices.Report(fixture.Store, fixture.Pair, "",
            new StorageNotice(fixture.Pair.A.RootId, "", fixture.Pair.B.RootId, "availability", cause),
            new StoragePairException(cause, "Synthetic pair cause"));
        fixture.Store.SetStorageRootBackoff(fixture.Pair.A.RootId, Deferred);
        if (!active) fixture.Suspend(disabled: false, suspended: true);
        var notice = fixture.Store.OpenNotices(fixture.Pair.PairId).Single(n => n.Storage?.Operation == "availability");
        var cli = new V2CliRuntime(null, fixture.Store, null!, null!, root);
        await cli.ResolveConflictAsync(fixture.Pair.PairId, "", NoticeChoices.RetryNow, notice.NoticeId, 0, null,
            CancellationToken.None);
        Require(fixture.Store.StorageRootBackedOff(fixture.Pair.A.RootId) == !clears,
            "cli pair cause " + cause + (active ? " on an active pair" : " on a paused pair") +
            (clears ? " clears the root backoff" : " keeps the root backoff"));
        Require(fixture.Calls == 0 && fixture.Pending(0) && fixture.Notice(0) is not null,
            "cli pair Retry for cause " + cause + " performs no native work and leaves item work untouched");
        Require(fixture.Store.NoticeById(fixture.Pair.PairId, notice.NoticeId!) is { Closed: false },
            "cli pair Retry for cause " + cause + " leaves the pair notice open");
    }

    /// <summary>A paused pair keeps a CLI item Retry queued, keeps the root backoff, and stores the same
    /// queued explanation the attended path records, so the app explains why nothing ran.</summary>
    private static async Task CliPausedItemAsync(string root)
    {
        using var fixture = new Fixture(root, Queue.Settlement);
        var notice = fixture.Notice(0) ?? throw new InvalidOperationException("Missing paused CLI fixture notice.");
        fixture.Suspend(disabled: false, suspended: true);
        fixture.Store.SetStorageRootBackoff(fixture.Pair.A.RootId, Deferred);
        var cli = new V2CliRuntime(null, fixture.Store, null!, null!, root);
        await cli.ResolveConflictAsync(fixture.Pair.PairId, notice.Path, NoticeChoices.RetryNow, notice.NoticeId, 0,
            null, CancellationToken.None);
        Require(fixture.Due(0) == StorageDue.UserRequested && fixture.Calls == 0,
            "cli paused-pair Retry persists the atomic request without native execution");
        Require(fixture.Store.StorageRootBackedOff(fixture.Pair.A.RootId),
            "cli paused-pair Retry keeps the root backoff");
        Require(fixture.Notice(0)?.Detail.Contains(StorageSettlementService.RetryQueuedMessage, StringComparison.Ordinal) == true,
            "cli paused-pair Retry stores the queued explanation on the notice");
        Require(fixture.Notice(0)?.NoticeId == notice.NoticeId,
            "cli paused-pair Retry preserves the open notice identity");
    }

    private static async Task ActiveAsync(string root, Queue queue)
    {
        using var fixture = new Fixture(root, queue);
        fixture.AssertDeferred();
        fixture.Store.SetStorageRootBackoff(fixture.Pair.A.RootId, Deferred);
        await using (var service = fixture.Service(targeted: true))
            await service.DrainAsync(CancellationToken.None);
        Require(fixture.Calls == 1 && !fixture.Pending(0), queue + ": active Retry executes exactly one deferred target despite root backoff");
        Require(fixture.Store.StorageRootBackedOff(fixture.Pair.A.RootId),
            queue + ": successful requested target preserves root backoff without a recorded connectivity cause");
        Require(fixture.Pending(1) && fixture.Due(1) > fixture.Clock.NowMs,
            queue + ": Retry leaves unrelated deferred entity untouched");
        Require(fixture.Notice(0) is null && fixture.Notice(1) is not null,
            queue + ": success closes only the target notice");
        await using (var background = fixture.Service())
            await background.DrainAsync(CancellationToken.None);
        Require(fixture.Calls == 1, queue + ": completed target does not run again on background turn");
    }

    private static async Task SuspendedAsync(string root, Queue queue, bool disabled)
    {
        using var fixture = new Fixture(root, queue);
        var noticeId = fixture.Notice(0)!.NoticeId;
        fixture.Suspend(disabled, true);
        fixture.Store.SetStorageRootBackoff(fixture.Pair.A.RootId, Deferred);
        await using (var service = fixture.Service(targeted: true))
        {
            try
            {
                await service.DrainAsync(CancellationToken.None);
                throw new InvalidOperationException(queue + ": suspended Retry must explain that it is queued");
            }
            catch (IOException error)
            {
                Require(error.Message == StorageSettlementService.RetryQueuedMessage,
                    queue + ": suspended Retry explains persistence and resume");
            }
        }
        Require(fixture.Calls == 0 && fixture.Due(0) == StorageDue.UserRequested,
            queue + ": suspended request persists the named sentinel without executing");
        Require(fixture.Notice(0)?.NoticeId == noticeId, queue + ": queued Retry preserves its open notice identity");
        Require(fixture.Page().Count == 0, queue + ": suspended root is excluded even for requested work");
        await using (var suspended = fixture.Service())
            await suspended.DrainAsync(CancellationToken.None);
        Require(fixture.Calls == 0 && fixture.Due(0) == StorageDue.UserRequested,
            queue + ": background admission cannot consume a suspended request");
        fixture.Suspend(disabled, false);
        await using (var resumed = fixture.Service())
            await resumed.DrainAsync(CancellationToken.None);
        await using (var next = fixture.Service())
            await next.DrainAsync(CancellationToken.None);
        Require(fixture.Calls == 1 && !fixture.Pending(0) && fixture.Pending(1),
            queue + ": ordinary resume executes saved request exactly once, not every deferred item");
        Require(fixture.Store.StorageRootBackedOff(fixture.Pair.A.RootId),
            queue + ": resumed requested target leaves unrelated root backoff intact");
    }

    private static async Task PausedDispatcherAsync(string root, Queue queue)
    {
        using var fixture = new Fixture(root, queue);
        fixture.Suspend(disabled: false, suspended: true);
        var original = fixture.Notice(0) ?? throw new InvalidOperationException("Missing dispatcher fixture notice.");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var current = fixture.Notice(0) ?? throw new InvalidOperationException("Queued notice disappeared.");
            try
            {
                await fixture.ApplyRetryAsync(current);
                throw new InvalidOperationException(queue + ": paused dispatcher must explain queued Retry");
            }
            catch (IOException error)
            {
                Require(error.Message == StorageSettlementService.RetryQueuedMessage,
                    queue + ": dispatcher returns the queued explanation");
            }
            var saved = fixture.Notice(0) ?? throw new InvalidOperationException("Dispatcher closed the queued notice.");
            var message = StorageSettlementService.RetryQueuedMessage;
            var first = saved.Detail.IndexOf(message, StringComparison.Ordinal);
            Require(saved.NoticeId == original.NoticeId && saved.ReasonKey == original.ReasonKey,
                queue + ": paused dispatcher preserves notice and cause identity");
            Require(first >= 0 && saved.Detail.IndexOf(message, first + message.Length, StringComparison.Ordinal) < 0,
                queue + ": repeated paused Retry stores its explanation exactly once");
            Require(fixture.Due(0) == StorageDue.UserRequested && fixture.Pending(0) && fixture.Calls == 0,
                queue + ": paused dispatcher persists the request without native execution");
        }
    }

    private static async Task AtomicRetryAsync(string root, Queue queue)
    {
        using var fixture = new Fixture(root, queue);
        if (queue == Queue.Refresh)
        {
            // Refresh failure reports the newly observed revision before the old receipt is replaced.
            // Retry targets the entity, not an equality check against that old receipt's scope.
            var metadata = fixture.Notice(0)?.Storage ?? throw new InvalidOperationException("Missing refresh notice.");
            var revisedRemote = fixture.Pointer(0).Remote with { Revision = "revision-2" };
            StorageNotices.Report(fixture.Store, fixture.Pair, fixture.Files[0].Path,
                metadata with { Scope = StorageNotices.ReceiptScope(revisedRemote.Id, revisedRemote.Revision, revisedRemote.ContentHash) },
                new IOException("Synthetic refresh failure after remote observation"));
        }
        var notice = fixture.Notice(0) ?? throw new InvalidOperationException(queue + ": missing dispatch fixture notice.");
        // A direct CLI has no native provider: it must queue, not try to execute with missing fixture
        // delegates or throw before saving the request. The background host alone claims the marker.
        Require(!fixture.RequestMissingJob() && fixture.Due(0) > fixture.Clock.NowMs,
            queue + ": a stale job id does not falsely report a persisted request");
        var cli = new V2CliRuntime(null, fixture.Store, null!, null!, root);
        await cli.ResolveConflictAsync(fixture.Pair.PairId, notice.Path, NoticeChoices.RetryNow,
            notice.NoticeId, 0, null, CancellationToken.None);
        Require(fixture.Due(0) == StorageDue.UserRequested && fixture.Pending(0) && fixture.Calls == 0,
            queue + ": direct CLI persists the atomic request without native execution");
        Require(fixture.Notice(0)?.NoticeId == notice.NoticeId && fixture.Due(1) > fixture.Clock.NowMs,
            queue + ": queued Retry preserves its notice and leaves unrelated work deferred");
        await using (var host = fixture.Service())
            await host.DrainAsync(CancellationToken.None);
        Require(fixture.Calls == 1 && !fixture.Pending(0) && fixture.Pending(1),
            queue + ": background host consumes exactly the CLI-requested target");
        Require(fixture.Notice(0) is null && fixture.Notice(1) is not null,
            queue + ": successful host retry closes only the executed target's notice");
        await using (var host = fixture.Service())
            await host.DrainAsync(CancellationToken.None);
        Require(fixture.Calls == 1, queue + ": another host turn cannot replay the consumed CLI request");
    }

    private static async Task FailedAsync(string root, Queue queue)
    {
        using var fixture = new Fixture(root, queue) { Fail = true };
        var noticeId = fixture.Notice(0)!.NoticeId;
        fixture.Store.SetStorageRootBackoff(fixture.Pair.A.RootId, Deferred);
        await using (var service = fixture.Service(targeted: true))
            await service.DrainAsync(CancellationToken.None);
        Require(fixture.Calls == 1 && fixture.Pending(0) && fixture.Due(0) > fixture.Clock.NowMs,
            queue + ": failed requested attempt becomes normally deferred rather than keeping the sentinel");
        Require(fixture.Notice(0)?.NoticeId == noticeId && fixture.Store.OpenNotices(fixture.Pair.PairId).Count == 2,
            queue + ": failure updates the same notice, without duplicates");
        fixture.Store.ClearStorageRootBackoff(fixture.Pair.A.RootId);
        Require(fixture.Page().Count == 0, queue + ": item due alone excludes the failed retry");
        await using (var automatic = fixture.Service())
            await automatic.DrainAsync(CancellationToken.None);
        Require(fixture.Calls == 1, queue + ": immediate background turn cannot repeat a failed Retry");
    }

    private static async Task PairCauseAsync(string root, Queue queue, string cause, bool succeeds, bool clearsPair)
    {
        using var fixture = new Fixture(root, queue) { Fail = !succeeds };
        var operation = queue == Queue.Offline ? StorageNotices.HydrationPairOperation : "availability";
        var metadata = new StorageNotice(fixture.Pair.A.RootId, "", fixture.Pair.B.RootId, operation, cause);
        StorageNotices.Report(fixture.Store, fixture.Pair, "", metadata,
            new StoragePairException(cause, "Synthetic pair cause"));
        fixture.Store.SetStorageRootBackoff(fixture.Pair.A.RootId, Deferred);
        var notice = fixture.Store.OpenNotices(fixture.Pair.PairId).Single(n => n.Storage?.Operation == operation);
        await using (var service = fixture.Service(targeted: true))
            await service.DrainAsync(CancellationToken.None);
        Require(fixture.Calls == 1 && fixture.Pending(0) == !succeeds,
            queue + ": cause-aware Retry executes its target once for " + cause);
        Require(fixture.Store.StorageRootBackedOff(fixture.Pair.A.RootId) == !clearsPair,
            queue + ": root backoff clears only after successful connectivity recovery, cause " + cause);
        var remaining = fixture.Store.OpenNotices(fixture.Pair.PairId)
            .Where(n => n.Storage?.Operation == operation).ToArray();
        Require(clearsPair ? remaining.Length == 0 : remaining.Length == 1 && remaining[0].NoticeId == notice.NoticeId,
            queue + ": pair notice closes only after successful connectivity recovery, cause " + cause);
        Require(fixture.Pending(1) && fixture.Notice(1) is not null,
            queue + ": pair recovery never consumes or closes unrelated item failures");
        if (!succeeds)
        {
            Require(fixture.Due(0) > fixture.Clock.NowMs,
                queue + ": unsuccessful connectivity Retry returns to ordinary due scheduling");
            await using var background = fixture.Service();
            await background.DrainAsync(CancellationToken.None);
            Require(fixture.Calls == 1, queue + ": failed connectivity Retry cannot run again immediately");
        }
    }

    private static async Task ConcurrentAsync(string root, Queue queue)
    {
        using var fixture = new Fixture(root, queue) { Fail = true };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = FilesOnDemandSettlement.SerializeAsync(fixture.Store, fixture.Pair.A.RootId,
            fixture.Files[0].ObjectId, async () =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(timeout.Token);
            }, timeout.Token);
        await entered.Task.WaitAsync(timeout.Token);
        await using var first = fixture.Service(targeted: true);
        await using var second = fixture.Service(targeted: true);
        await using var background = fixture.Service();
        Task firstRun = Task.CompletedTask, secondRun = Task.CompletedTask, backgroundRun = Task.CompletedTask;
        try
        {
            // DrainAsync persists the request before its first await at the held entity gate.
            firstRun = first.DrainAsync(timeout.Token);
            secondRun = second.DrainAsync(timeout.Token);
            backgroundRun = background.DrainAsync(timeout.Token);
            Require(!firstRun.IsCompleted && !secondRun.IsCompleted,
                queue + ": both targeted callers overlap behind the shared entity gate");
            Require(fixture.Due(0) == StorageDue.UserRequested && fixture.Calls == 0,
                queue + ": overlapping requests coalesce before the claim");
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(held, firstRun, secondRun, backgroundRun).WaitAsync(timeout.Token);
        }
        Require(fixture.Calls == 1 && fixture.Pending(0) && fixture.Due(0) > fixture.Clock.NowMs,
            queue + ": concurrent CLI/background callers consume one request, including when execution fails");
        Require(fixture.Pending(1) && fixture.Store.OpenNotices(fixture.Pair.PairId).Count == 2,
            queue + ": concurrent Retry preserves unrelated work and deduplicated notices");
    }

    private static void Priority(string root, Queue queue)
    {
        using var fixture = new Fixture(root, queue);
        fixture.Defer(0, TimeSpan.Zero);
        fixture.Defer(1, TimeSpan.Zero);
        var normal = fixture.Page();
        Require(normal.Count == 2, queue + ": priority fixture has two ordinary due jobs");
        var cursorJob = fixture.JobId(1);
        var cursor = queue == Queue.Refresh
            ? SqliteStateStore.StoragePointerCursor(fixture.Pointer(1)) : "";
        // A higher-id request precedes lower-id ordinary work even on a one-item page.
        fixture.Request(1);
        Require(fixture.Page(limit: 1).Single() == fixture.Files[1].ObjectId,
            queue + ": explicit Retry has strict priority over normal work");
        fixture.Defer(1, TimeSpan.Zero);
        fixture.Request(0);
        Require(fixture.Page(cursorJob, cursor, 1).Single() == fixture.Files[0].ObjectId,
            queue + ": requested work behind an already-advanced cursor is not starved");
        fixture.Store.SetStorageRootBackoff(fixture.Pair.A.RootId, Deferred);
        Require(fixture.Page().SequenceEqual(new[] { fixture.Files[0].ObjectId }),
            queue + ": only requested work bypasses root backoff");
        Require(fixture.Page(activeRoots: []).Count == 0,
            queue + ": explicit active-root exclusion still wins over request priority");
        fixture.Suspend(disabled: false, suspended: true);
        Require(fixture.Page().Count == 0, queue + ": paused root excludes requested work");
        fixture.Suspend(disabled: false, suspended: false);
        fixture.Suspend(disabled: true, suspended: true);
        Require(fixture.Page().Count == 0, queue + ": disabled root excludes requested work");
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly RetryClock Clock = new();
        internal readonly SqliteStateStore Store;
        internal readonly PairDef Pair;
        internal readonly ItemState[] Files;
        private readonly Queue _queue;
        private readonly string _root;
        private readonly CloudFilesLifetime _connections;
        internal int Calls;
        internal bool Fail;

        internal Fixture(string root, Queue queue)
        {
            if (Directory.Exists(root)) throw new InvalidOperationException("Use a fresh disposable retry fixture directory.");
            _root = root;
            _queue = queue;
            Store = SqliteStateStore.Open(root, V2Settings.Default, Clock);
            var local = new RootDef("local", SyncProviders.Local, null, root, "id:local:00000001:0000000000000001");
            var remote = new RootDef("remote", SyncProviders.Dropbox, "account", "/Remote", "remote-root");
            Pair = new("pair", "Pair A", local, remote, [], [], true);
            Store.SavePair(Pair);
            Store.EnableStorage();
            var hash = new string('a', 64);
            Files = new[] { "0000000000000002", "00000000000000ae" }.Select((id, i) =>
                new ItemState($"file-{i}.bin", ItemKind.File, "id:local:00000001:" + id, null,
                    hash, 4, DateTimeOffset.UnixEpoch, queue == Queue.Offline ? ItemFlags.OwnedPointer : ItemFlags.None)).ToArray();
            Store.WriteSnapshot(new(local.RootId, Side.A, Store.BeginGeneration(local.RootId, "snapshot"), "local", Files, []));
            var remoteFiles = Files.Select((file, i) => file with
            {
                ObjectId = "remote-file-" + i, VersionId = "revision-1", Flags = ItemFlags.None
            }).ToArray();
            var generation = Store.BeginGeneration(remote.RootId, "snapshot");
            Store.WriteSnapshot(new(remote.RootId, Side.B, generation, "remote", remoteFiles, []));
            if (queue != Queue.Refresh)
            {
                Store.SetStoragePolicy(local.RootId, local.RootObjectId, queue == Queue.Offline ? "offline" : "online-only");
                Store.EnqueueStoragePolicyPage();
            }
            if (queue != Queue.Offline)
            {
                for (var i = 0; i < Files.Length; i++)
                {
                    var receipt = new PilotRemoteEntry(Files[i].Path, PilotEntryKind.File,
                        remoteFiles[i].ObjectId, "revision-1", 4, hash);
                    Store.QueueStorageSettlement(local.RootId, Files[i].ObjectId, remote.RootId, receipt);
                    if (queue == Queue.Refresh)
                        Require(Store.CompleteStorageSettlement(Settlement(i), new("OnlineOnly", false, true)),
                            "refresh fixture records its confirmed pointer");
                }
            }
            if (queue == Queue.Refresh)
            {
                var changes = remoteFiles.Select(file => new Change(ChangeKind.Upsert, file.ObjectId,
                    file.Path, file with { VersionId = "revision-2" }, null)).ToArray();
                Store.WriteDelta(new(remote.RootId, Side.B, generation, "remote", "changed", changes, false, []));
            }
            _connections = new CloudFilesLifetime((_, _, _) =>
                throw new InvalidOperationException("Explicit Retry fixture must never open a native provider."), _ => false, Store);
            for (var i = 0; i < Files.Length; i++)
            {
                Require(Pending(i), queue + ": fixture queued each entity");
                Defer(i, Deferred);
                StorageNotices.Report(Store, Pair, Files[i].Path, Target(i), Failure());
            }
        }

        internal Task ApplyRetryAsync(Notice notice) => StorageNoticeChoices.ApplyAsync(Store, _connections,
            notice, NoticeChoices.RetryNow, CancellationToken.None);

        internal StorageSettlementService Service(bool targeted = false) => new(Store, _connections,
            target: targeted ? Target(0) : null,
            convert: (_, _) =>
            {
                Attempt();
                return Task.FromResult(new StorageObserved("OnlineOnly", false, true));
            },
            hydrate: (_, _, validate, _) =>
            {
                validate();
                Attempt();
                return Task.FromResult(new StorageObserved("AvailableOffline", true, true));
            },
            refresh: (job, _, _) =>
            {
                Attempt();
                return Task.FromResult<PilotRemoteEntry?>(job.Remote);
            });

        private void Attempt()
        {
            Require(Due(0) > Clock.NowMs, _queue + ": effect starts only after the request is atomically claimed");
            Interlocked.Increment(ref Calls);
            if (Fail) throw Failure();
        }
        private static IOException Failure() => new("Synthetic item retry failure");
        internal StorageSettlement Settlement(int i) => Store.StorageSettlementFor(Pair.A.RootId, Files[i].ObjectId, Pair.B.RootId)
            ?? throw new InvalidOperationException("Missing settlement fixture job.");
        internal StorageSettlement Pointer(int i) => Store.StoragePointerFor(Pair.A.RootId, Files[i].ObjectId, Pair.B.RootId)
            ?? throw new InvalidOperationException("Missing pointer fixture job.");
        internal StorageHydrationJob Hydration(int i) => Store.StorageHydrationFor(Pair.A.RootId, Files[i].ObjectId)
            ?? throw new InvalidOperationException("Missing hydration fixture job.");
        internal long JobId(int i) => _queue switch
        {
            Queue.Settlement => Settlement(i).JobId,
            Queue.Offline => Hydration(i).JobId,
            _ => Pointer(i).JobId
        };
        internal bool Pending(int i) => _queue switch
        {
            Queue.Settlement => Store.StorageSettlementFor(Pair.A.RootId, Files[i].ObjectId, Pair.B.RootId) is not null,
            Queue.Offline => Store.StorageHydrationFor(Pair.A.RootId, Files[i].ObjectId) is not null,
            _ => Store.StoragePointerFor(Pair.A.RootId, Files[i].ObjectId, Pair.B.RootId) is { JobId: > 0 }
        };
        private StorageNotice Target(int i)
        {
            var scope = "offline";
            if (_queue != Queue.Offline)
            {
                var remote = _queue == Queue.Settlement ? Settlement(i).Remote : Pointer(i).Remote;
                scope = StorageNotices.ReceiptScope(remote.Id, remote.Revision, remote.ContentHash);
            }
            return new(Pair.A.RootId, Files[i].ObjectId, Pair.B.RootId,
                _queue switch { Queue.Settlement => "settlement", Queue.Offline => "offline", _ => "remote-update" }, scope);
        }
        internal Notice? Notice(int i) => Store.OpenNotices(Pair.PairId).SingleOrDefault(n => n.Storage?.ObjectId == Files[i].ObjectId);
        internal void Suspend(bool disabled, bool suspended)
        {
            if (disabled) Store.SavePair(Pair with { Enabled = !suspended });
            else Store.SetPairPaused(Pair.PairId, suspended);
        }
        internal void Defer(int i, TimeSpan delay)
        {
            switch (_queue)
            {
                case Queue.Settlement: Store.DeferStorageSettlement(Settlement(i), delay); break;
                case Queue.Offline: Store.DeferStorageHydration(Hydration(i), delay); break;
                default: Store.DeferStoragePointer(Pointer(i), delay); break;
            }
        }
        internal bool RequestMissingJob() => _queue switch
        {
            Queue.Settlement => Store.RequestStorageSettlement(Settlement(0) with { JobId = long.MaxValue }),
            Queue.Offline => Store.RequestStorageHydration(Hydration(0) with { JobId = long.MaxValue }),
            _ => Store.RequestStoragePointer(Pointer(0) with { JobId = long.MaxValue })
        };
        internal void Request(int i)
        {
            switch (_queue)
            {
                case Queue.Settlement: Store.RequestStorageSettlement(Settlement(i)); break;
                case Queue.Offline: Store.RequestStorageHydration(Hydration(i)); break;
                default: Store.RequestStoragePointer(Pointer(i)); break;
            }
        }
        internal IReadOnlyList<string> Page(long after = 0, string pointerAfter = "", int limit = 128,
            IReadOnlyCollection<string>? activeRoots = null) => _queue switch
        {
            Queue.Settlement => Store.StorageSettlementPage(after, limit, activeRoots).Select(job => job.ObjectId).ToArray(),
            Queue.Offline => Store.StorageHydrationPage(after, limit, activeRoots).Select(job => job.ObjectId).ToArray(),
            _ => Store.ChangedStoragePointers(pointerAfter, limit, activeRoots).Select(job => job.ObjectId).ToArray()
        };
        internal long Due(int i)
        {
            var table = _queue switch { Queue.Settlement => "storage_settlement", Queue.Offline => "storage_hydration", _ => "storage_refresh" };
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = SqliteStateStore.DatabasePath(_root), Mode = SqliteOpenMode.ReadOnly
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT due FROM {table} WHERE root_id=$root AND object_id=$object;";
            command.Parameters.AddWithValue("$root", Pair.A.RootId);
            command.Parameters.AddWithValue("$object", Files[i].ObjectId);
            return command.ExecuteScalar() is long due ? due : throw new InvalidOperationException("Missing due fixture row.");
        }
        internal void AssertDeferred()
        {
            Require(Page().Count == 0, _queue + ": deferred fixture is absent from background pages");
            if (_queue != Queue.Settlement)
                Require(!Store.HasStorageWork(), _queue + ": target must bypass aggregate due-work gate");
        }
        public void Dispose() { _connections.Dispose(); Store.Dispose(); }
    }

    private sealed class RetryClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddDays(1);
        internal long NowMs => GetUtcNow().ToUnixTimeMilliseconds();
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
