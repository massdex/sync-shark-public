namespace Massdex.SyncShark.V2;

public sealed record StorageNotice(string RootId, string ObjectId, string RemoteBinding, string Operation, string? Scope = null);

internal sealed class StoragePairException(string cause, string message, Exception? inner = null) : IOException(message, inner)
{
    internal string Cause { get; } = cause;
}

/// <summary>Shared storage notice vocabulary and actions; no delete or upload authority.</summary>
internal static class StorageNotices
{
    internal static string ReceiptScope(PilotRemoteEntry remote) =>
        ShortId.Make("receipt", remote.Id ?? "", remote.Revision ?? "", remote.ContentHash ?? "");
    private static string Family(StorageNotice item) => "storage:" + item.Operation + ":" +
        ShortId.Make("item", item.RootId, item.ObjectId, item.RemoteBinding) + ":";
    internal static string Prefix(StorageNotice item) => Family(item) + (item.Scope is null ? "" : item.Scope + ":");

    internal static void Report(SqliteStateStore store, PairDef pair, string path, StorageNotice item, Exception error)
    {
        if (error is StoragePairException pairError)
        {
            item = item with { ObjectId = "", Operation = "availability", Scope = pairError.Cause };
            path = "";
        }
        var reason = Prefix(item) + ShortId.Make("cause", error.GetType().Name,
            error.HResult.ToString(System.Globalization.CultureInfo.InvariantCulture), error.Message);
        if (store.StorageCauseIgnored(pair.PairId, reason)) return;
        store.AddNotices([new Notice(pair.PairId, 42, NoticeKind.Info, path,
            "Cloud storage " + item.Operation + " failed: " + error.Message +
            " Correct the cause and Retry verification/settlement, or Ignore this cause. Local bytes are retained and no upload is repeated.",
            reason, Storage: item)]);
    }

    // A successful current item observation closes obsolete revisions/causes as well.
    internal static void Complete(SqliteStateStore store, string pairId, StorageNotice item) =>
        store.CloseNoticeFamily(pairId, Family(item));
    internal static void CompletePair(SqliteStateStore store, PairDef pair, string operation) =>
        Complete(store, pair.PairId, new(pair.A.RootId, "", pair.B.RootId, "availability"));

    internal static async Task ApplyAsync(SqliteStateStore store, CloudFilesLifetime connections,
        Notice notice, string choice, CancellationToken ct, Func<PairDef, bool>? paused = null)
    {
        var item = notice.Storage ?? throw new InvalidOperationException("Not a storage notice.");
        if (choice is not (NoticeChoices.Ignore or NoticeChoices.RetryNow))
            throw new InvalidOperationException("Choose Retry or Ignore; nothing was changed.");
        var pair = store.Pair(notice.PairId);
        if (pair is null || pair.A.RootId != item.RootId || pair.B.RootId != item.RemoteBinding)
        {
            store.CloseNotice(notice.NoticeId!, choice);
            return; // Obsolete binding notices cannot require work against a different pair.
        }
        if (choice == NoticeChoices.Ignore)
        {
            // Ignore suppresses this exact cause, not all future work on this identity.
            // Automatic retries can still succeed or expose a different cause/revision.
            store.CloseNotice(notice.NoticeId!, choice);
            return;
        }
        if (item.Operation == "hydration")
        {
            var local = store.ByObject(item.RootId, item.ObjectId);
            if (local is null)
            {
                Complete(store, pair.PairId, item);
                return;
            }
            var full = LocalItems.FullPath(pair.A.RootPath, local.Path);
            if (!WindowsCloudFilesProvider.TryReadOwnedPlaceholder(full, out var pointer) || pointer is null)
            {
                Complete(store, pair.PairId, item);
                return;
            }
            if (!pointer.Identity.IsBoundTo(pair.PairId, pair.B.Provider) ||
                pointer.FileId != LocalItems.ParseObjectId(item.ObjectId).FileId)
                throw new IOException("The owned pointer changed. Rescan before Retry.");
            // Probe the exact retained revision through the remote byte endpoint. A cached
            // local range cannot prove that a previously failing download is available again.
            await connections.RequireProvider(pair, ct).VerifyHydrationAsync(pointer.Identity, ct).ConfigureAwait(false);
            Complete(store, pair.PairId, item);
            return;
        }
        if (item.ObjectId.Length == 0)
        {
            // A pair-level Retry probes the pair once; it never drains every item in that pair.
            if (!pair.Enabled || store.PairPaused(pair.PairId) || (paused?.Invoke(pair) ?? false))
                throw new IOException("Storage intent is saved. Enable or resume this pair before Retry.");
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(pair.A.RootPath))!);
            if (drive.DriveType != DriveType.Fixed || drive.DriveFormat != "NTFS")
                throw new IOException("Online-only storage requires a fixed NTFS volume. Keep offline or move the pair, then Retry.");
            connections.RequireProvider(pair, ct);
            Complete(store, pair.PairId, item);
            return;
        }
        if (item.Operation == "settlement")
        {
            var pending = store.StorageSettlementFor(item.RootId, item.ObjectId, item.RemoteBinding);
            if (pending is null || item.Scope is not null && ReceiptScope(pending.Remote) != item.Scope)
            {
                Complete(store, pair.PairId, item);
                return;
            }
            store.RetryStorageSettlement(item.RootId, item.ObjectId, item.RemoteBinding);
        }
        await using var service = new StorageSettlementService(store, connections, target: item, paused: paused);
        await service.DrainAsync(ct).ConfigureAwait(false);
        if (store.NoticeById(pair.PairId, notice.NoticeId!) is { Closed: false })
            throw new IOException("Storage verification remains pending. Correct the reported cause and Retry; the item is unchanged.");
    }
}
