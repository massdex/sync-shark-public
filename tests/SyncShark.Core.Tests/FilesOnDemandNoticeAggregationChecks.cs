using System.Text.Json;
using Massdex.SyncShark;
using Massdex.SyncShark.V2;

/// <summary>Round 5: more than twenty pointer notices become one pair summary; entity debts stay durable.</summary>
internal static class FilesOnDemandNoticeAggregationChecks
{
    internal static async Task RunAsync(string root)
    {
        if (Directory.Exists(root)) throw new InvalidOperationException("Use a fresh disposable database directory.");
#if !SYNCSHARK_FOD_STANDALONE
        // Core.Tests-only regression: it depends on the large RunChecks/FakeEndpoint graph, which the
        // standalone Files On-Demand host deliberately does not link. Every aggregation check below runs.
        await RunChecks.PointerObservationRetryAsync(Path.Combine(root, "observation-retry"));
#endif
        const int items = StorageNotices.NoticeAggregateLimit + 1;
        var localRoot = new RootDef("local", SyncProviders.Local, null, root, "id:local:00000001:0000000000000001");
        var remoteRoot = new RootDef("remote", SyncProviders.Dropbox, "account", "/Remote", "remote-root");
        var pair = new PairDef("pair", "Pair A", localRoot, remoteRoot, [], [], true);
        using var store = SqliteStateStore.Open(root, V2Settings.Default, TimeProvider.System);
        store.SavePair(pair);
        var generation = store.BeginGeneration(localRoot.RootId, "snapshot");
        var states = Enumerable.Range(0, items).Select(index => new ItemState(
            "Folder/file-" + index, ItemKind.File, "file-" + index, null, null, 4,
            DateTimeOffset.UnixEpoch, ItemFlags.OwnedPointer)).ToArray();
        store.WriteSnapshot(new Snapshot(localRoot.RootId, Side.A, generation, "cursor", states, []));
        // The online-only command opts the store in and creates the storage schema before receipts are written.
        store.SetStoragePolicy(localRoot.RootId, localRoot.RootObjectId, "online-only");
        var receipts = states.Select((state, index) => new PilotRemoteEntry(state.Path, PilotEntryKind.File,
            "id:file-" + index, "revision-" + index, 4, new string('a', 64))).ToArray();
        WriteReceipts(root, localRoot.RootId, remoteRoot.RootId, states, receipts);
        // Written debt is durable queue state, not a snapshot flag: establish it the same way production does.
        foreach (var state in states)
            store.RequireRehash(localRoot.RootId, store.ByObject(localRoot.RootId, state.ObjectId)!, "verification pending");

        // Model an older binary's existing per-file flood: observing any existing item must
        // collapse it too, not only a newly encountered twenty-first entity.
        store.AddNotices(states.Select((state, index) =>
        {
            var metadata = new StorageNotice(localRoot.RootId, state.ObjectId, remoteRoot.RootId,
                StorageNotices.PointerVerificationOperation, StorageNotices.ReceiptScope(receipts[index]));
            return new Notice(pair.PairId, 42, NoticeKind.Info, state.Path, "Verification pending",
                StorageNotices.Prefix(metadata) + "existing", Storage: metadata);
        }));
        store.RequestPointerVerification(pair.PairId, localRoot.RootId, states[0].ObjectId, states[0].Path, receipts[0]);
        var notices = store.OpenNotices(pair.PairId).Where(notice => notice.Storage is not null).ToArray();
        Require(notices.Length == 1 && notices[0].Storage!.Operation == StorageNotices.PointerVerificationPairOperation,
            "more than twenty pointer-verification items collapse to one pair summary");
        Require(!PlanNotices.HasPathScope(notices[0]), "the pair summary carries no path hold");
        Require(notices[0].Detail.Contains("More than 20", StringComparison.Ordinal), "summary states the threshold");

        await StorageNotices.ApplyAsync(store, null, notices[0], NoticeChoices.RetryNow, CancellationToken.None);
        foreach (var state in states)
        {
            var hydration = store.StorageHydrationFor(localRoot.RootId, state.ObjectId);
            Require(hydration is not null && store.IsStorageHydrationRequested(hydration),
                "aggregate Retry persists a requested hydration job for every entity");
        }
        Require((store.ByObject(localRoot.RootId, states[0].ObjectId)!.Flags & ItemFlags.NeedsRehash) != 0,
            "aggregate Retry leaves the durable entity debt unchanged");
        Require(store.OpenNotices(pair.PairId).Any(notice =>
                notice.Storage?.Operation == StorageNotices.PointerVerificationPairOperation),
            "summary stays open until verification succeeds");

        foreach (var state in states)
            Require(store.CompleteRehash(localRoot.RootId, state, new string('b', 64)), "exact verification settles one entity");
        StorageNotices.Complete(store, pair.PairId, new(localRoot.RootId, states[0].ObjectId, remoteRoot.RootId,
            StorageNotices.PointerVerificationOperation));
        Require(!store.OpenNotices(pair.PairId).Any(notice => notice.Storage is not null),
            "successful verification clears the summary once every debt is settled");

        // Ignore on the summary silences this pair cause; the durable per-entity debts are untouched.
        foreach (var state in states)
            store.RequireRehash(localRoot.RootId, store.ByObject(localRoot.RootId, state.ObjectId)!, "verification pending");
        for (var index = 0; index < items; index++)
            store.RequestPointerVerification(pair.PairId, localRoot.RootId, states[index].ObjectId, states[index].Path, receipts[index]);
        notices = store.OpenNotices(pair.PairId).Where(notice => notice.Storage is not null).ToArray();
        Require(notices.Length == 1 && notices[0].Storage!.Operation == StorageNotices.PointerVerificationPairOperation,
            "the summary is re-raised while the debt stays above the limit");
        await StorageNotices.ApplyAsync(store, null, notices[0], NoticeChoices.Ignore, CancellationToken.None);
        Require(!store.OpenNotices(pair.PairId).Any(notice => notice.Storage is not null),
            "Ignore closes the pair summary");
        store.RequestPointerVerification(pair.PairId, localRoot.RootId, states[0].ObjectId, states[0].Path, receipts[0]);
        Require(!store.OpenNotices(pair.PairId).Any(notice => notice.Storage is not null),
            "an ignored pair cause stays suppressed while the debt remains durable");
        Require((store.ByObject(localRoot.RootId, states[0].ObjectId)!.Flags & ItemFlags.NeedsRehash) != 0,
            "Ignore leaves every entity retry debt in place");

        // Transient scanner observations: one durable SyncRetry per entity, one summary notice above the limit.
        for (var index = 0; index < items; index++)
            store.SaveSyncRetry(pair.PairId, new SyncRetry("Folder/file-" + index, "scan", 1,
                OwnedPointerProof.ObservationMessage + " Correct the reported access, placeholder or name issue, then Retry; " +
                "Ignore excludes this scope from sync.", DateTimeOffset.UtcNow.AddMinutes(5)));
        store.AddNotices(Enumerable.Range(0, items).Select(index => new Notice(pair.PairId, 40, NoticeKind.Info,
            "Folder/file-" + index, OwnedPointerProof.ObservationMessage +
            " Nothing in this subtree was synced or deleted. Options: retry now / exclude / ignore.", "sync-retry")));
        var summary = new Notice(pair.PairId, 40, NoticeKind.Info, string.Empty,
            items + " online-only files could not be observed because their metadata was temporarily unavailable.",
            NoticeChoices.PointerObservationSummary);
        store.AddNotices([summary]);
        Require(store.OwnedPointerObservationSummaryState(pair.PairId) == ObservationSummaryState.Open,
            "an open pair summary is visible to the next scan");
        var options = NoticeChoices.Options(summary);
        Require(options.Count == 2 && options.Any(option => option.Choice == NoticeChoices.RetryNow) &&
            options.Any(option => option.Choice == NoticeChoices.Ignore),
            "pair summary offers Retry and Ignore without a path exclusion");
        store.RequestOwnedPointerObservationRetriesNow(pair.PairId, OwnedPointerProof.ObservationMessage);
        var due = store.SyncRetryAt(pair.PairId, "Folder/file-0")!;
        Require(due.RetryAfter == DateTimeOffset.MinValue && due.Kind == "scan" && due.Attempts == 1 &&
            due.LastError.StartsWith(OwnedPointerProof.ObservationMessage, StringComparison.Ordinal),
            "pair Retry makes every transient entity retry due at once and round-trips its JSON");
        store.CloseOwnedPointerObservationNotices(pair.PairId, OwnedPointerProof.ObservationMessage);
        Require(!store.OpenNotices(pair.PairId).Any(notice => notice.ReasonKey == "sync-retry" && notice.Path.Length > 0),
            "the pair summary replaces every per-file transient notice");
        Require(store.OpenNotices(pair.PairId).Any(notice => notice.ReasonKey == NoticeChoices.PointerObservationSummary),
            "the pair summary survives per-file notice closure");
        store.CloseOwnedPointerObservationSummary(pair.PairId);
        Require(!store.OpenNotices(pair.PairId).Any(notice =>
                notice.ReasonKey is "sync-retry" or NoticeChoices.PointerObservationSummary),
            "a later clean scan clears the pair summary");
        store.AddNotices([summary with { Detail = summary.Detail + " again" }]);
        var reopened = store.OpenNotices(pair.PairId).Single(notice =>
            notice.ReasonKey == NoticeChoices.PointerObservationSummary);
        store.CloseNotice(reopened.NoticeId!, NoticeChoices.Ignore);
        Require(store.OwnedPointerObservationSummaryState(pair.PairId) == ObservationSummaryState.Ignored,
            "an ignored pair summary stays suppressed instead of deaggregating into per-file notices");
        store.AddNotices([summary with { Detail = summary.Detail + " later" }]);
        Require(!store.OpenNotices(pair.PairId).Any(notice => notice.ReasonKey == NoticeChoices.PointerObservationSummary),
            "an ignored pair summary is never reopened by a later scan");
        Console.WriteLine("PASS pointer-verification and transient-observation notices aggregate above twenty " +
            "while durable entity retries and their clearing behaviour stay unchanged");
    }

    private static void WriteReceipts(string root, string localRoot, string remoteRoot,
        IReadOnlyList<ItemState> states, IReadOnlyList<PilotRemoteEntry> receipts)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = SqliteStateStore.DatabasePath(root) }.ToString());
        connection.Open();
        for (var index = 0; index < states.Count; index++)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO storage_item(root_id,object_id,remote_binding,remote_json,availability,pinned,in_sync) " +
                "VALUES($r,$o,$b,$j,'OnlineOnly',0,1);";
            command.Parameters.AddWithValue("$r", localRoot);
            command.Parameters.AddWithValue("$o", states[index].ObjectId);
            command.Parameters.AddWithValue("$b", remoteRoot);
            command.Parameters.AddWithValue("$j", JsonSerializer.Serialize(receipts[index]));
            command.ExecuteNonQuery();
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
