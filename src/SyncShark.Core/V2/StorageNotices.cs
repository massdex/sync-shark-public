namespace Massdex.SyncShark.V2;

public sealed record StorageNotice(string RootId, string ObjectId, string RemoteBinding, string Operation);

/// <summary>Shared storage notice vocabulary and actions; no delete or upload authority.</summary>
internal static class StorageNotices
{
    internal static string Prefix(StorageNotice item) => "storage:" + item.Operation + ":" +
        ShortId.Make("item", item.RootId, item.ObjectId, item.RemoteBinding) + ":";

    internal static void Report(SqliteStateStore store, PairDef pair, string path, StorageNotice item, Exception error)
    {
        if (store.StorageOperationIgnored(pair.PairId, item)) return;
        store.AddNotices([new Notice(pair.PairId, 42, NoticeKind.Info, path,
            "Cloud storage " + item.Operation + " failed: " + error.Message +
            " Correct the cause and Retry verification/settlement, or Ignore to leave the item unchanged. No file is deleted and no upload is repeated.",
            Prefix(item) + error.GetType().Name + ":" + error.HResult, Storage: item)]);
    }

    internal static void Complete(SqliteStateStore store, string pairId, StorageNotice item) =>
        store.CloseNoticeFamily(pairId, Prefix(item));

    internal static async Task ApplyAsync(SqliteStateStore store, CloudFilesLifetime connections,
        Notice notice, string choice, CancellationToken ct)
    {
        var item = notice.Storage ?? throw new InvalidOperationException("Not a storage notice.");
        if (choice is not (NoticeChoices.Ignore or NoticeChoices.RetryNow))
            throw new InvalidOperationException("Choose Retry or Ignore; nothing was changed.");
        var pair = store.Pair(notice.PairId) ?? throw new IOException("The pair is no longer configured.");
        if (pair.A.RootId != item.RootId || pair.B.RootId != item.RemoteBinding)
            throw new IOException("The storage binding changed; no file was changed.");
        if (choice == NoticeChoices.Ignore)
        {
            if (item.Operation == "settlement")
                store.IgnoreStorageSettlement(item.RootId, item.ObjectId, item.RemoteBinding);
            store.CloseNotice(notice.NoticeId!, choice);
            return;
        }
        if (item.Operation == "hydration")
        {
            var local = store.ByObject(item.RootId, item.ObjectId)
                ?? throw new IOException("The item identity is not indexed. Rescan before Retry.");
            var full = LocalItems.FullPath(pair.A.RootPath, local.Path);
            if (!WindowsCloudFilesProvider.TryReadOwnedPlaceholder(full, out var pointer) || pointer is null ||
                !pointer.Identity.IsBoundTo(pair.PairId, pair.B.Provider) ||
                pointer.FileId != LocalItems.ParseObjectId(item.ObjectId).FileId)
                throw new IOException("The owned pointer changed. Rescan before Retry.");
            connections.RequireProvider(pair, ct);
            // Foreground verification uses Windows hydration, independently of the sync run gate.
            await using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SyncContentIdentity.ComputeAsync(pair.B.Provider, stream, ct).ConfigureAwait(false);
            if (hash != pointer.Identity.ContentHash || stream.Length != pointer.Identity.LogicalSize)
                throw new IOException("Hydration verification differs from the recorded revision; retain the local bytes and rescan.");
            Complete(store, pair.PairId, item);
            return;
        }
        if (item.Operation == "settlement")
            store.RetryStorageSettlement(item.RootId, item.ObjectId, item.RemoteBinding);
        await using var service = new StorageSettlementService(store, connections, target: item);
        await service.DrainAsync(ct).ConfigureAwait(false);
        if (store.NoticeById(pair.PairId, notice.NoticeId!) is { Closed: false })
            throw new IOException("Storage verification remains pending. Correct the reported cause and Retry; the item is unchanged.");
    }
}
