using Massdex.SyncShark;
using Massdex.SyncShark.V2;

internal static class FilesOnDemandStorageChecks
{
    internal static async Task RunAsync(string root)
    {
        if (Directory.Exists(root)) throw new InvalidOperationException("Use a fresh disposable database directory.");
        FilesOnDemandCompatibilityChecks.Run(Path.Combine(root, "v20-fixture"));
        FilesOnDemandLegacyIndexChecks.Run(Path.Combine(root, "legacy-indexes"));
        await FilesOnDemandServiceChecks.RunAsync(Path.Combine(root, "service"));
        await StorageExplicitRetryChecks.RunAsync(Path.Combine(root, "explicit-retry"));
        await StorageQueuePlanChecks.RunAsync(Path.Combine(root, "queue-plans"));
        CloudFilesOwnershipChecks.Run();
        OwnedPointerProofChecks.Run();
        await NewPointerDownloadChecks.RunAsync();
        await PointerUpdateCoordinationChecks.RunAsync(Path.Combine(root, "pointer-coordination"));
        await SettlementInventoryChecks.RunAsync(Path.Combine(root, "settlement-inventory"));
        await HydrationNoticeWriterChecks.RunAsync(Path.Combine(root, "hydration-notices"));
        await FilesOnDemandSettlementChecks.RunAsync(Path.Combine(root, "settlement-service"));
        await FilesOnDemandNoticeAggregationChecks.RunAsync(Path.Combine(root, "notice-aggregation"));
        FilesOnDemandProtectionChecks.Run(Path.Combine(root, "pointer-protection"));
        await StoreWorkChecksAsync(Path.Combine(root, "store-work"));
        var remote = new PilotRemoteEntry("Folder/file.txt", PilotEntryKind.File, "id:file", "revision-1", 4, new string('a', 64));
        var local = new RootDef("local", SyncProviders.Local, null, root, "root-object");
        using (var store = SqliteStateStore.Open(root, V2Settings.Default, TimeProvider.System))
        {
            foreach (var verb in new[] { "online-only", "offline", "inherit", "list" })
            {
                var args = verb == "list" ? new[] { "storage", verb, "--pair", "pair" }
                    : new[] { "storage", verb, "--pair", "pair", "--path", "Folder" };
                Require(SyncCliRegistry.Parse(["--cli", .. args]).Command == "storage " + verb, "CLI intent syntax without legacy account/generation");
            }
            var generation = store.BeginGeneration(local.RootId, "snapshot");
            var items = Enumerable.Range(0, 10000).Select(i => new ItemState("Folder/file-" + i,
                ItemKind.File, "file-" + i, null, null, 4, DateTimeOffset.UnixEpoch, ItemFlags.None)).ToList();
            items.Add(new ItemState("Folder", ItemKind.Folder, "folder-object", null, null, 0,
                DateTimeOffset.UnixEpoch, ItemFlags.None));
            store.WriteSnapshot(new Snapshot(local.RootId, Side.A, generation, "cursor", items, []));
            store.SavePair(new PairDef("pair", "Pair A", local,
                new RootDef("remote", SyncProviders.Dropbox, "account", "/Remote", "remote-root"), [], [], true));
            // The intent path needs no run object or endpoint. It cannot accidentally wait on a run gate.
            var cli = new V2CliRuntime(null, store, null!, null!, root);
            await cli.SetStorageIntentAsync("pair", "Folder", "online-only", CancellationToken.None,
                Path.Combine(root, "Recovery"), false);
            if (OperatingSystem.IsWindows() && new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!).DriveType == DriveType.Removable)
            {
                Require(store.StorageFolderSummaries(local).Count == 1, "removable volume retains online-only intent for later eligibility");
                Console.WriteLine("PASS removable-volume intent persists without native conversion");
            }
            var summary = store.StorageFolderSummaries(local).Single();
            Require(summary.Items == 10000 && summary.Unknown == 10000, "SQL list counts 10000 unobserved files without claiming hydration");
            // Generate the scale fixture inside SQLite, never a million managed ItemState objects.
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = SqliteStateStore.DatabasePath(root) }.ToString()))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandTimeout = 120;
                command.CommandText = """
                    WITH RECURSIVE numbers(n) AS (SELECT 10000 UNION ALL SELECT n+1 FROM numbers WHERE n<999999)
                    INSERT INTO file_state(root_no,dir_no,name_key,name,kind,object_id,size,mtime,flags)
                    SELECT f.root_no,f.dir_no,'FILE-'||n,'file-'||n,0,'file-'||n,4,0,0
                    FROM numbers CROSS JOIN (SELECT root_no,dir_no FROM file_state WHERE object_id='file-0' LIMIT 1) f;
                    """;
                command.ExecuteNonQuery();
            }
            summary = store.StorageFolderSummaries(local).Single();
            Require(summary.Items == 1000000 && summary.Unknown == 1000000, "SQL list aggregates one million rows");
            Console.WriteLine("PASS SQL storage list aggregation at 1,000,000 rows");
            store.SetStoragePolicyAtPath(local, "Folder", "inherit");
            Require(store.StorageFolderSummaries(local).Count == 0, "inherit removes explicit folder listing");
            store.SetStoragePolicy(local.RootId, local.RootObjectId, "online-only");
            Require(store.ResolveStoragePolicy(local, "Folder/file.txt") == "online-only", "root inheritance");
            store.SetStoragePolicy(local.RootId, local.RootObjectId, "inherit");
            Require(store.ResolveStoragePolicy(local, "Folder/file.txt") == "offline", "default remains offline");
            store.SetStoragePolicy(local.RootId, local.RootObjectId, "online-only");
            var source = new ItemState(remote.RelativePath, ItemKind.File, "file-object", null,
                remote.ContentHash, remote.Size, DateTimeOffset.UnixEpoch, ItemFlags.None);
            var uploaded = source with { ObjectId = remote.Id!, VersionId = remote.Revision };
            var upload = new SyncAction("upload-receipt", 1, ActionKind.CopyNew, Side.B,
                source.Path, null, source, null, false, source.ObjectId);
            var endpoint = new ReceiptEndpoint(uploaded);
            store.RecordIntent("pair", upload);
            try { await endpoint.PutAsync(upload, Stream.Null, CancellationToken.None); }
            catch (UncertainEffectException) { }
            store.CommitResult("pair", generation, upload,
                new ActionResult(upload.ActionId, Outcome.Uncertain, null, null, null, "response lost"));
            Require(store.StorageSettlementPage().Count == 0, "uncertain upload cannot authorize conversion");
            var executor = new Executor(store, V2Settings.Default,
                new SyncTransferResourceController(new SyncTransferSettings()));
            var reconciled = await executor.ReconcileAsync([upload], generation, endpoint, endpoint, CancellationToken.None);
            Require(reconciled.Single().Outcome == Outcome.Done && endpoint.UploadCalls == 1,
                "lost response reconciles remote identity without a second upload");
            Require(store.StorageSettlementPage().Single().Remote.Revision == remote.Revision,
                "confirmed result durably schedules its exact revision");
            store.QueueStorageSettlement(local.RootId, "file-object", "remote", remote);
            Require(store.StorageSettlementPage().Count == 1, "duplicate confirmed receipt deduplicates conversion work");
            var metadata = new StorageNotice(local.RootId, "file-object", "remote", "settlement");
            for (var attempt = 0; attempt < 2; attempt++)
                StorageNotices.Report(store, store.Pair("pair")!, remote.RelativePath, metadata, new IOException("synthetic conversion failure"));
            Require(store.OpenNotices("pair").Count(n => n.Storage is not null) == 1, "storage failure deduplicates by item operation cause");
        }
        using (var store = SqliteStateStore.Open(root, V2Settings.Default, TimeProvider.System))
        {
            Require(store.StorageSettlementPage().Count == 1, "receipt survives restart before conversion");
            var savedNotice = store.OpenNotices("pair").Single(n => n.Storage is not null);
            Require(savedNotice.Storage!.ObjectId == "file-object" && NoticeChoices.Options(savedNotice).Count == 2,
                "durable storage metadata and Retry/Ignore options survive restart");
            var savedSettlement = store.StorageSettlementFor(local.RootId, "file-object", "remote");
            using (var connections = new CloudFilesLifetime((_, _, _) => throw new InvalidOperationException("Ignore must not connect"), _ => false))
                await StorageNotices.ApplyAsync(store, connections, savedNotice, NoticeChoices.Ignore, CancellationToken.None);
            // E9: Ignore retains open/actionable authority, hiding only the cause's presentation.
            Require(store.StorageSettlementPage().Count == 1 &&
                store.StorageSettlementFor(local.RootId, "file-object", "remote") == savedSettlement &&
                store.NoticeById("pair", savedNotice.NoticeId!) is { Closed: false } &&
                store.OpenNotices("pair").All(n => n.NoticeId != savedNotice.NoticeId) &&
                store.UnresolvedNotices("pair").Any(n => n.NoticeId == savedNotice.NoticeId),
                "shared Ignore suppresses only the cause without opening provider");
            StorageNotices.Report(store, store.Pair("pair")!, remote.RelativePath, savedNotice.Storage!, new IOException("synthetic conversion failure"));
            Require(!store.OpenNotices("pair").Any(n => n.Storage is not null), "ignored storage cause remains deduplicated");
            store.CloseNotice(savedNotice.NoticeId!, "resolved");
            store.RequestStorageSettlement(store.StorageSettlementFor(local.RootId, "file-object", "remote")
                ?? throw new InvalidOperationException("Missing settlement retry fixture."));
            var skippedConversions = 0;
            var skipped = new FilesOnDemandSettlement(store, (_, _) =>
            {
                Interlocked.Increment(ref skippedConversions);
                return Task.FromResult(new StorageObserved("OnlineOnly", false, true));
            }, (_, _) => throw new InvalidOperationException("Ineligible jobs should not fail."), _ => false);
            await skipped.RunPageAsync(0, CancellationToken.None);
            Require(skippedConversions == 0 && store.StorageSettlementPage().Count == 1,
                "paused or temporarily ineligible work is retained without converting");
            var failures = 0;
            var worker = new FilesOnDemandSettlement(store, (_, _) => throw new IOException("edited after upload"), (_, _) => failures++);
            await worker.RunPageAsync(0, CancellationToken.None);
            Require(failures == 1 && store.StorageSettlementPage().Count == 1, "edit during conversion retains durable work");
            var old = store.StorageSettlementPage()[0];
            store.QueueStorageSettlement(local.RootId, "file-object", "remote", remote with { Revision = "revision-2" });
            store.CompleteStorageSettlement(old, new("OnlineOnly", false, true));
            Require(store.StorageSettlementPage().Count == 1, "stale completion cannot consume newer receipt");
            StorageNotices.Report(store, store.Pair("pair")!, remote.RelativePath, savedNotice.Storage!, new IOException("conversion retry"));
            worker = new FilesOnDemandSettlement(store, (_, _) => Task.FromResult(new StorageObserved("OnlineOnly", false, true)), (_, _) => failures++,
                completed: _ => StorageNotices.Complete(store, "pair", savedNotice.Storage!));
            await worker.RunPageAsync(0, CancellationToken.None);
            Require(store.StorageSettlementPage().Count == 0 && failures == 1, "successful conversion acknowledges exact receipt");
            Require(!store.OpenNotices("pair").Any(n => n.Storage is not null), "successful settlement closes its notice family");
            for (var i = 0; i < 257; i++) store.QueueStorageSettlement(local.RootId, "object-" + i, "binding", remote);
            var first = store.StorageSettlementPage();
            var second = store.StorageSettlementPage(first[^1].JobId);
            Require(first.Count == 128 && second.Count == 128 && first[^1].JobId < second[0].JobId, "keyset pages bounded and disjoint");
            var concurrent = 0;
            var arrivals = 0;
            var fourStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            worker = new FilesOnDemandSettlement(store, async (_, token) =>
            {
                var active = Interlocked.Increment(ref concurrent);
                if (active > 4) throw new InvalidOperationException("Settlement exceeded its worker bound.");
                if (Interlocked.Increment(ref arrivals) == 4) fourStarted.TrySetResult();
                await fourStarted.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
                Interlocked.Decrement(ref concurrent);
                return new StorageObserved("OnlineOnly", false, true);
            }, (_, error) => throw new InvalidOperationException("Concurrent settlement failed.", error));
            await worker.RunPageAsync(0, CancellationToken.None);
            Require(arrivals == 128 && store.StorageSettlementPage(limit: 512).Count == 129,
                "settlement runs independent items concurrently within one bounded page");
        }
        Console.WriteLine("PASS storage CLI syntax/intent, SQL list at 10000 files, default/inheritance, receipt deduplication, restart, failed conversion, stale completion and bounded settlement pages (fake upload response loss and production reconciliation/store path; no live provider upload exercised)");
    }
    private static Task StoreWorkChecksAsync(string root)
    {
        using var store = SqliteStateStore.Open(root, V2Settings.Default, TimeProvider.System);
        var localRoot = new RootDef("device", SyncProviders.Local, null, root, "device-root");
        var cloudRoot = new RootDef("cloud", SyncProviders.Dropbox, "account", "/Remote", "cloud-root");
        var pair = new PairDef("work", "Pair A", localRoot, cloudRoot, [], [], true);
        store.SavePair(pair);
        var local = new ItemState("Folder/file", ItemKind.File, "local-file", null, new string('b', 64), 4, DateTimeOffset.UnixEpoch, ItemFlags.None);
        var cloud = local with { ObjectId = "cloud-file", VersionId = "revision-1" };
        var folder = local with { Path = "Folder", Kind = ItemKind.Folder, ObjectId = "folder", Hash = null, Size = 0 };
        var localGen = store.BeginGeneration(localRoot.RootId, "snapshot");
        var cloudGen = store.BeginGeneration(cloudRoot.RootId, "snapshot");
        store.WriteSnapshot(new(localRoot.RootId, Side.A, localGen, "local", [folder, local], []));
        store.WriteSnapshot(new(cloudRoot.RootId, Side.B, cloudGen, "cloud", [cloud], []));
        var upload = new SyncAction("default-upload", 1, ActionKind.CopyNew, Side.B, local.Path, null, local, null, false, local.ObjectId);
        store.RecordIntent(pair.PairId, upload);
        store.CommitResult(pair.PairId, localGen, upload, new(upload.ActionId, Outcome.Done, local, cloud, null, null));
        Require(!store.HasStorageWork() && store.StorageSettlementPage().Count == 0,
            "default offline upload writes no receipt or background work");
        store.SetStoragePolicyAtPath(localRoot, "Folder", "online-only");
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = SqliteStateStore.DatabasePath(root) }.ToString()))
        {
            connection.Open();
            var plan = string.Join("\n", StoreSchema.QueryPlan(connection, "storage-work", SqliteStateStore.HasStorageWorkSql, "$now"));
            Require(plan.Contains("storage_online_policy") && plan.Contains("storage_pending") && plan.Contains("storage_refresh_due"),
                "cheap work existence uses policy, pending and due indexes");
        }
        Require(store.EnqueueStoragePolicyPage() > 0 && store.StorageSettlementPage().Count == 1,
            "setting online-only enqueues existing fully verified baseline without upload");
        Require(store.StorageSettlementPage(activeRoots: []).Count == 0, "inactive roots are excluded before item paging");
        store.SetStorageRootBackoff(localRoot.RootId, TimeSpan.FromHours(1));
        Require(store.StorageSettlementPage().Count == 0, "root backoff excludes pending work");
        store.ClearStorageRootBackoff(localRoot.RootId);
        var old = store.StorageSettlementPage().Single();
        store.CompleteStorageSettlement(old, new("OnlineOnly", false, true));
        Require(store.ChangedStoragePointers().Count == 0, "unchanged pointers are not scanned as refresh work");
        var changedCloud = cloud with { VersionId = "revision-2" };
        store.WriteDelta(new(cloudRoot.RootId, Side.B, cloudGen, "cloud", "next",
            [new(ChangeKind.Upsert, cloud.ObjectId, cloud.Path, changedCloud, null)], false, []));
        var dirty = store.ChangedStoragePointers().Single();
        Require(dirty.Remote.Revision == "revision-1", "remote delta drives indexed dirty pointer queue");
        store.CompleteStoragePointerUpdate(dirty, dirty.Remote with { Revision = "revision-2" });
        Require(store.ChangedStoragePointers().Count == 0, "pointer completion acknowledges exact dirty version");
        store.SetStoragePolicy(localRoot.RootId, local.ObjectId, "online-only");
        var replacement = local with { ObjectId = "downloaded-file" };
        store.WriteDelta(new(localRoot.RootId, Side.A, localGen, "local", "next",
            [new(ChangeKind.Upsert, replacement.ObjectId, replacement.Path, replacement, null)], false, []));
        Require(store.StoragePointerFor(localRoot.RootId, local.ObjectId, cloudRoot.RootId) is null &&
            store.StorageSettlementFor(localRoot.RootId, local.ObjectId, cloudRoot.RootId) is null,
            "replacement retires old identity storage rows");
        var download = new SyncAction("download-receipt", 2, ActionKind.Update, Side.A, local.Path, null, changedCloud, local, false, local.ObjectId);
        store.RecordIntent(pair.PairId, download);
        store.CommitResult(pair.PairId, localGen, download, new(download.ActionId, Outcome.Done, replacement, changedCloud, null, null));
        Require(store.StorageSettlementPage().Single().ObjectId == replacement.ObjectId,
            "confirmed download queues new local identity under inherited online-only policy");
        store.SetStoragePolicyAtPath(localRoot, "Later", "online-only");
        Require(store.HasUnresolvedStorageIntent(localRoot, "Later"), "unindexed path intent persists");
        Require(store.UnresolvedStorageIntents(localRoot).Single().Path == "Later", "unresolved intent appears in bounded list");
        store.SetStoragePolicyAtPath(localRoot, "Typo", "online-only");
        store.SetStoragePolicyAtPath(localRoot, "Typo", "inherit");
        Require(!store.HasUnresolvedStorageIntent(localRoot, "Typo"), "inherit removes unresolved intent");
        var later = folder with { Path = "Later", ObjectId = "later-folder" };
        store.WriteDelta(new(localRoot.RootId, Side.A, localGen, "next", "later",
            [new(ChangeKind.Upsert, later.ObjectId, later.Path, later, null)], false, []));
        Require(!store.HasUnresolvedStorageIntent(localRoot, "Later") && store.ResolveStoragePolicy(localRoot, "Later/new") == "online-only",
            "indexed identity binds persisted intent without refusal");
        store.WriteDelta(new(localRoot.RootId, Side.A, localGen, "later", "gone",
            [new(ChangeKind.Delete, later.ObjectId, later.Path, null, DateTimeOffset.UtcNow)], false, []));
        Require(store.ResolveStoragePolicy(localRoot, "Later/new") == "offline", "deleted folder policy cannot leak to replacement path");
        Console.WriteLine("PASS opt-in receipt, indexed existence, baseline/download enqueue, dirty pointer queue, replacement cleanup and unresolved intent checks");
        return Task.CompletedTask;
    }

    private sealed class ReceiptEndpoint(ItemState published) : IEndpointWriter
    {
        public int UploadCalls { get; private set; }
        public Task<ItemState> PutAsync(SyncAction action, Stream content, CancellationToken ct)
        {
            UploadCalls++;
            throw new UncertainEffectException("Upload committed but response was lost.");
        }
        public Task<ItemState?> ObserveAsync(string path, CancellationToken ct) => Task.FromResult<ItemState?>(published);
        public Task<Stream> OpenReadAsync(ItemState state, CancellationToken ct) => throw new InvalidOperationException("Reconciliation must not read an upload source.");
        public Task<ItemState> MoveAsync(SyncAction action, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(SyncAction action, CancellationToken ct) => throw new NotSupportedException();
        public Task<ItemState> MkDirAsync(string path, CancellationToken ct) => throw new NotSupportedException();
        public Task<RecoveryRef> PreserveAsync(ItemState victim, string actionId, CancellationToken ct) => throw new NotSupportedException();
    }

    private static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException(description);
    }
}
