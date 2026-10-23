using System.Net;
using System.Net.Sockets;
using Massdex.SyncShark;
using Massdex.SyncShark.V2;

/// <summary>
/// Dedicated review-2 checks for the bounded hydration notice lane: N7 pair-level transport/auth/provider failures are
/// ONE pair notice and item-specific faults stay item notices; N8 one open notice per item+operation updated in place
/// with ignored causes still suppressed; N11 the open cache is identity-owned so a renamed item's success closes the
/// old notice without resolving the placeholder; N6 the hydration pair family (including the overflow backlog) closes
/// on a successful hydration, prior-process causes included.
/// </summary>
internal static class StorageNoticeLaneChecks
{
    internal static Task RunAsync(string root)
    {
        if (Directory.Exists(root)) throw new InvalidOperationException("Use a fresh disposable database directory.");
        Directory.CreateDirectory(root);
        var local = new RootDef("local", SyncProviders.Local, null, root, "root-object");
        using var store = SqliteStateStore.Open(root, V2Settings.Default, TimeProvider.System);
        store.EnableStorage();
        store.SavePair(new PairDef("pair", "Pair A", local,
            new RootDef("remote", SyncProviders.Dropbox, "fixture-account", "/Remote", "remote-root"), [], [], true));
        var pair = store.Pair("pair")!;
        var item = new StorageNotice(local.RootId, "id:local:00000001:0000000000000001", pair.B.RootId, "hydration");

        PairCauseIsOneNotice(store, pair);

        // N8: a new cause updates the family's one open notice in place (stable id), and Ignore stays cause-specific.
        StorageNotices.Report(store, pair, "Folder/one.bin", item, new IOException("cause one"));
        var first = SingleStorageNotice(store, pair);
        StorageNotices.Report(store, pair, "Folder/one.bin", item, new IOException("cause two"));
        var updated = SingleStorageNotice(store, pair);
        HydrationNoticeWriterChecks.Require(updated.NoticeId == first.NoticeId,
            "a new cause updates the same notice identity instead of adding a second open notice");
        HydrationNoticeWriterChecks.Require(updated.Detail.Contains("cause two", StringComparison.Ordinal),
            "the updated notice shows the current cause");
        store.CloseNotice(updated.NoticeId!, NoticeChoices.Ignore);
        StorageNotices.Report(store, pair, "Folder/one.bin", item, new IOException("cause two"));
        HydrationNoticeWriterChecks.Require(StorageStorage(store, pair).Count == 0,
            "an ignored exact cause is never reopened");
        StorageNotices.Report(store, pair, "Folder/one.bin", item, new IOException("cause one"));
        HydrationNoticeWriterChecks.Require(StorageStorage(store, pair).Count == 1,
            "a different cause stays visible beside ignored history");
        StorageNotices.Complete(store, pair.PairId, item);
        HydrationNoticeWriterChecks.Require(StorageStorage(store, pair).Count == 0, "item completion clears the family");

        // Ignored cause A, then an open cause B, then cause A again: suppressing A must also close the obsolete
        // open B in the same transition, and A must remain ignored (a later A still opens nothing).
        StorageNotices.Report(store, pair, "Folder/flip.bin", item, new IOException("flip cause A"));
        var ignoredA = SingleStorageNotice(store, pair);
        store.CloseNotice(ignoredA.NoticeId!, NoticeChoices.Ignore);
        StorageNotices.Report(store, pair, "Folder/flip.bin", item, new IOException("flip cause B"));
        var openB = SingleStorageNotice(store, pair);
        HydrationNoticeWriterChecks.Require(openB.NoticeId != ignoredA.NoticeId,
            "a different cause opens beside the ignored cause");
        StorageNotices.Report(store, pair, "Folder/flip.bin", item, new IOException("flip cause A"));
        HydrationNoticeWriterChecks.Require(StorageStorage(store, pair).Count == 0,
            "suppressing cause A closes the obsolete open cause B in the same transition");
        StorageNotices.Report(store, pair, "Folder/flip.bin", item, new IOException("flip cause A"));
        HydrationNoticeWriterChecks.Require(StorageStorage(store, pair).Count == 0,
            "the ignored cause A remains suppressed after the transition");
        StorageNotices.Complete(store, pair.PairId, item);
        HydrationNoticeWriterChecks.Require(StorageStorage(store, pair).Count == 0, "flip fixture is clean");

        // N6/N11 through the writer: a renamed item closes by callback identity; a prior-process pair cause closes on
        // the first successful hydration of a new writer.
        var objectId = "id:local:00000001:00000000000000ae";
        using (var writer = new HydrationNoticeWriter(store, startWorker: false))
        {
            writer.Register(pair);
            writer.Prime("pair");
            writer.Report(pair, "Folder/old.bin", new IOException("range mismatch"),
                _ => item with { ObjectId = objectId }, objectId);
            writer.Drain();
            HydrationNoticeWriterChecks.Require(StorageStorage(store, pair).Count == 1,
                "the item failure is open before the rename");
            HydrationNoticeWriterChecks.Require(writer.Succeeded("pair", "Folder/new.bin",
                    _ => throw new InvalidOperationException("a renamed success must not open the placeholder"),
                    objectId),
                "the renamed success is tracked by identity, not by the old path");
            writer.Drain();
            HydrationNoticeWriterChecks.Require(writer.ClosedNotices == 1 && StorageStorage(store, pair).Count == 0,
                "the renamed item's success closes the old notice without a placeholder resolve");
        }

        // A pair-level hydration cause left by a previous process closes on the first success of a fresh writer.
        StorageNotices.Report(store, pair, string.Empty,
            new StorageNotice(local.RootId, string.Empty, pair.B.RootId, StorageNotices.HydrationPairOperation),
            new StoragePairException("network", "prior process offline"));
        HydrationNoticeWriterChecks.Require(StorageStorage(store, pair).Count == 1,
            "the prior-process pair cause is open before recovery");
        using (var writer = new HydrationNoticeWriter(store, startWorker: false))
        {
            writer.Register(pair);
            writer.Succeeded("pair", "Folder/recovered.bin", null, objectId);
            writer.Drain();
            HydrationNoticeWriterChecks.Require(writer.ClosedPairCauses == 1 &&
                StorageStorage(store, pair).Count == 0,
                "a successful hydration closes the prior-process hydration pair cause");
        }

        // The writer's overflow/backlog pair cause is the same family and closes on the next successful read.
        // A local notice backlog is not connectivity evidence: it must not root-backoff the other lanes.
        store.ClearStorageRootBackoff(pair.A.RootId);
        using (var writer = new HydrationNoticeWriter(store, startWorker: false))
        {
            writer.Register(pair);
            writer.Prime("pair");
            writer.Report(pair, "Folder/busy.bin",
                new StoragePairException("backlog", "queue busy"), _ => null);
            writer.Drain();
            HydrationNoticeWriterChecks.Require(!store.StorageRootBackedOff(pair.A.RootId),
                "a local notice backlog never root-backs-off settlement, refresh or offline");
            var backlog = SingleStorageNotice(store, pair);
            HydrationNoticeWriterChecks.Require(backlog.Storage is { Operation: StorageNotices.HydrationPairOperation, ObjectId: "" },
                "the backlog is one pair-scoped hydration notice");
            writer.Succeeded("pair", "Folder/busy.bin", null, objectId);
            writer.Drain();
            HydrationNoticeWriterChecks.Require(StorageStorage(store, pair).Count == 0,
                "a later successful hydration clears the backlog pair notice");
        }

        Console.WriteLine("PASS storage notice lane: pair causes are one notice, one notice per item+operation updates in " +
            "place with ignored suppression, renamed identity closes, and backlog/prior-process pair causes clear on success");
        return Task.CompletedTask;
    }

    private static void PairCauseIsOneNotice(SqliteStateStore store, PairDef pair)
    {
        // Positive transport/auth/provider evidence classifies as a pair cause; item-specific faults never do.
        HydrationNoticeWriterChecks.Require(Cause(StorageNotices.ClassifyFailure(
                new HttpRequestException("denied", null, HttpStatusCode.Unauthorized))) == "auth",
            "an HTTP 401 is account auth");
        HydrationNoticeWriterChecks.Require(Cause(StorageNotices.ClassifyFailure(
                new DropboxApiException(HttpStatusCode.Unauthorized, "token")
                { OAuthError = DropboxOAuthError.InvalidGrant })) == "auth",
            "an OAuth invalid_grant is account auth");
        HydrationNoticeWriterChecks.Require(Cause(StorageNotices.ClassifyFailure(
                new EndpointAccessException("access refused"))) == "auth",
            "the mapped account-access refusal is auth");
        HydrationNoticeWriterChecks.Require(Cause(StorageNotices.ClassifyFailure(
                new DropboxCredentialUnavailableException("connection"))) == "auth",
            "a missing protected credential is auth");
        HydrationNoticeWriterChecks.Require(Cause(StorageNotices.ClassifyFailure(
                new HttpRequestException("offline", null, HttpStatusCode.ServiceUnavailable))) == "network",
            "an HTTP 503 is transport");
        HydrationNoticeWriterChecks.Require(Cause(StorageNotices.ClassifyFailure(
                new HttpRequestException("offline"))) == "network",
            "a connection failure without a status is transport");
        HydrationNoticeWriterChecks.Require(Cause(StorageNotices.ClassifyFailure(
                new ProviderTransportTimeoutException("stalled"))) == "network",
            "a bounded read timeout is transport");
        HydrationNoticeWriterChecks.Require(Cause(StorageNotices.ClassifyFailure(
                new SocketException((int)SocketError.HostUnreachable))) == "network",
            "a socket failure is transport");
        HydrationNoticeWriterChecks.Require(Cause(StorageNotices.ClassifyFailure(
                new SyncRunRefusedException("The Dropbox connection is not connected."))) == "provider",
            "a disconnected provider is pair-level");
        HydrationNoticeWriterChecks.Require(StorageNotices.ClassifyFailure(
                new InvalidDataException("The expected Dropbox range identity is incomplete.")) is null,
            "a missing item revision/hash stays item-specific");
        HydrationNoticeWriterChecks.Require(StorageNotices.ClassifyFailure(
                new DropboxApiException(HttpStatusCode.Conflict, "changed")) is null,
            "a remote change conflict stays item-specific");
        HydrationNoticeWriterChecks.Require(StorageNotices.ClassifyFailure(
                new IOException("The hydrated range differs from the exact provider placeholder identity.")) is null,
            "a range mismatch stays item-specific");

        // Transport failures for ten different files coalesce to one queued pair cause and one durable notice.
        using (var writer = new HydrationNoticeWriter(store, startWorker: false))
        {
            writer.Register(pair);
            writer.Prime("pair");
            for (var index = 0; index < 10; index++)
                writer.Report(pair, "Folder/offline-" + index + ".bin",
                    new HttpRequestException("offline", null, HttpStatusCode.ServiceUnavailable), _ => null);
            HydrationNoticeWriterChecks.Require(writer.PendingEvents == 1,
                "an offline burst coalesces to one queued pair cause, not one per file");
            writer.Drain();
        }
        var network = SingleStorageNotice(store, pair);
        HydrationNoticeWriterChecks.Require(network.Path.Length == 0 &&
            network.Storage is { Operation: StorageNotices.HydrationPairOperation, ObjectId: "", Scope: "network" },
            "a transport failure is ONE pair notice, not a per-item failure");
        HydrationNoticeWriterChecks.Require(store.StorageRootBackedOff(pair.A.RootId),
            "the same offline burst durably backs off the local root");

        // A different pair-level cause updates that same notice.
        StorageNotices.Report(store, pair, "Folder/auth.bin",
            new StorageNotice(pair.A.RootId, "object-auth", pair.B.RootId, "hydration"),
            new StoragePairException("auth", "sign-in refused"));
        var auth = SingleStorageNotice(store, pair);
        HydrationNoticeWriterChecks.Require(auth.NoticeId == network.NoticeId && auth.Storage!.Scope == "auth",
            "account auth updates the one pair notice in place");
        StorageNotices.Report(store, pair, "Folder/provider.bin",
            new StorageNotice(pair.A.RootId, "object-provider", pair.B.RootId, "hydration"),
            new StoragePairException("provider", "connection unavailable"));
        var provider = SingleStorageNotice(store, pair);
        HydrationNoticeWriterChecks.Require(provider.NoticeId == network.NoticeId && provider.Storage!.Scope == "provider",
            "a disconnected provider updates the one pair notice in place");

        // An item fault (missing revision/hash, conflict) is not promoted to a pair cause.
        StorageNotices.Report(store, pair, "Folder/item.bin",
            new StorageNotice(pair.A.RootId, "id:local:00000001:0000000000000002", pair.B.RootId, "hydration"),
            new InvalidDataException("The expected Dropbox range identity is incomplete."));
        HydrationNoticeWriterChecks.Require(StorageStorage(store, pair).Count == 2,
            "an item-specific fault remains its own item notice");
        StorageNotices.Complete(store, pair.PairId,
            new StorageNotice(pair.A.RootId, "id:local:00000001:0000000000000002", pair.B.RootId, "hydration"));
        StorageNotices.CompleteHydrationPair(store, pair);
        HydrationNoticeWriterChecks.Require(StorageStorage(store, pair).Count == 0, "lane fixture is clean");
    }

    private static string? Cause(StoragePairException? error) => error?.Cause;

    private static Notice SingleStorageNotice(SqliteStateStore store, PairDef pair)
    {
        var notices = StorageStorage(store, pair);
        HydrationNoticeWriterChecks.Require(notices.Count == 1, "exactly one storage notice is open");
        return notices[0];
    }

    private static List<Notice> StorageStorage(SqliteStateStore store, PairDef pair) =>
        store.OpenNotices(pair.PairId).Where(notice => notice.Storage is not null).ToList();
}
