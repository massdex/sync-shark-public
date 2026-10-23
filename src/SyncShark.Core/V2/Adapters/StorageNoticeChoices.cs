using static Massdex.SyncShark.V2.StorageNotices;

namespace Massdex.SyncShark.V2;

internal static class StorageNoticeChoices
{
    internal static async Task ApplyAsync(SqliteStateStore store, CloudFilesLifetime? connections,
        Notice notice, string choice, CancellationToken ct, Func<PairDef, bool>? paused = null,
        Action<string>? progress = null)
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
        if (item.Operation == PointerVerificationOperation)
        {
            ct.ThrowIfCancellationRequested();
            if (!store.QueuePointerVerificationHydration(item.RootId, item.ObjectId))
            {
                Complete(store, pair.PairId, item);
                return;
            }
            // Reuse the durable offline queue and its request sentinel, not callback retry or a new lane.
            item = item with { Operation = "offline", Scope = "offline" };
        }
        if (item.Operation == PointerVerificationPairOperation)
        {
            // One explicit Retry applies the same durable request to every affected entity. Each entity keeps
            // its own debt; the summary closes only after verification actually succeeds.
            ct.ThrowIfCancellationRequested();
            var queued = store.QueuePointerVerificationHydrationForPair(item.RootId, item.RemoteBinding);
            if (queued == 0)
            {
                Complete(store, pair.PairId, item);
                return;
            }
            progress?.Invoke($"Verification requested for {queued} online-only file(s); it clears when they verify. " +
                "Each file keeps its durable retry debt and no local edit is discarded.");
            return;
        }
        if (connections is null)
        {
            ct.ThrowIfCancellationRequested();
            // Direct CLI cannot perform native work. Persist the request, without claiming it, so the
            // background host can consume the same atomic sentinel used by attended Retry.
            var queued = false;
            if (item.Operation == "settlement" &&
                store.StorageSettlementFor(item.RootId, item.ObjectId, item.RemoteBinding) is { } settlement &&
                (item.Scope is null || ReceiptScope(settlement.Remote) == item.Scope))
            {
                queued = store.RequestStorageSettlement(settlement);
            }
            // A refresh failure can name the newly observed revision while the receipt still holds the
            // old revision. Request the current entity job; its normal revalidation protects the revision.
            else if (item.Operation == "remote-update" &&
                store.StoragePointerFor(item.RootId, item.ObjectId, item.RemoteBinding) is { JobId: > 0 } pointerJob)
            {
                queued = store.RequestStoragePointer(pointerJob);
            }
            else if (item.Operation == "offline" &&
                store.StorageHydrationFor(item.RootId, item.ObjectId) is { } hydrationJob)
            {
                queued = store.RequestStorageHydration(hydrationJob);
            }
            // A settlement notice whose row is gone (already settled, or the identity was purged) or whose
            // scope names a different receipt has nothing left to verify: mirror the attended store-only close.
            else if (item.Operation == "settlement")
            {
                Complete(store, pair.PairId, item);
                return;
            }
            var inactivePair = !pair.Enabled || store.PairPaused(pair.PairId) || (paused?.Invoke(pair) ?? false);
            string message;
            if (queued)
            {
                // The app records the same explanation on the notice (RecordNoticeChoiceFailure) so it explains
                // why nothing ran; keep the CLI path consistent and store it once.
                if (inactivePair && !notice.Detail.Contains(StorageSettlementService.RetryQueuedMessage, StringComparison.Ordinal))
                    store.RecordNoticeChoiceFailure(notice, StorageSettlementService.RetryQueuedMessage);
                message = inactivePair
                    ? "Retry saved; it will run when the pair resumes. Start the Sync Shark background app with autosync start, or open the app."
                    : "Retry saved. It will run when the Sync Shark background app starts (autosync start) or the next time the app runs.";
            }
            else if (item.ObjectId.Length == 0)
            {
                // Only connectivity evidence lets an explicit Retry drop the root backoff, and only for an active
                // pair: binding, volume, host, backlog and unknown causes are not fixed by retrying.
                if ((item.Scope is "network" or "auth" or "provider") && !inactivePair)
                {
                    store.ClearStorageRootBackoff(item.RootId);
                    message = "The pair retry delay is cleared. Start the background app with autosync start, or open the app; " +
                        "pending work retries on its normal schedule. Retry this notice in the app to verify the pair immediately.";
                }
                else if (inactivePair)
                    message = "This pair is paused or disabled, so the pair verification cannot run. Enable or resume it, " +
                        "then Retry; the pair retry delay is unchanged.";
                else
                    message = "The cause shown in this notice is not a connectivity failure, so Retry cannot clear the " +
                        "pair retry delay. Correct the cause, then Retry in the app; the delay stays in place.";
            }
            else
                message = "This verification needs the Sync Shark background app. Start it with autosync start, or open the app, " +
                    "then Retry this notice there. The notice stays open; no verification was performed.";
            progress?.Invoke(message);
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
            // A pair-level Retry probes the pair once; it never drains every item in that pair. Hydration is
            // deliberately independent of pair/global pause, so the pause gate applies only to settlement's
            // availability family, never to the hydration-pair network/auth/provider/backlog cause.
            var hydration = item.Operation == HydrationPairOperation;
            if (!hydration && (!pair.Enabled || store.PairPaused(pair.PairId) || (paused?.Invoke(pair) ?? false)))
                throw new IOException("Storage intent is saved. Enable or resume this pair before Retry.");
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(pair.A.RootPath))!);
            if (drive.DriveType != DriveType.Fixed || drive.DriveFormat != "NTFS")
                throw new IOException("Online-only storage requires a fixed NTFS volume. Keep offline or move the pair, then Retry.");
            connections.RequireProvider(pair, ct);
            // A pair-level cause is cleared only after one bounded source check, never from a bare native open.
            if (connections.ObserveRemote is not { } observe)
                throw new IOException("Remote verification is unavailable. Reconnect the provider, then Retry.");
            await observe(pair, ".sync-shark-storage-retry-probe", ct).ConfigureAwait(false);
            store.ClearStorageRootBackoff(pair.A.RootId);
            if (hydration) CompleteHydrationPair(store, pair);
            else Complete(store, pair.PairId, item);
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
            // Do not move the row due here: DrainAsync below writes the durable UserRequested sentinel under
            // its own atomic claim. A due=0 pre-write would let a background page claim the row as Normal in
            // between, so a later success in this turn could fail to clear the connectivity cause.
        }
        // A paused/disabled pair keeps the item request queued. Update the existing notice row (same id and
        // reason) with the explanation the service throws, without appending it again on repeated Retry.
        var inactive = !pair.Enabled || store.PairPaused(pair.PairId) || (paused?.Invoke(pair) ?? false);
        if (inactive && !notice.Detail.Contains(StorageSettlementService.RetryQueuedMessage, StringComparison.Ordinal))
            store.RecordNoticeChoiceFailure(notice, StorageSettlementService.RetryQueuedMessage);
        // Targeted drain: "settlement", "remote-update" and the durable "offline" operation are all item jobs.
        await using var service = new StorageSettlementService(store, connections, target: item, paused: paused);
        await service.DrainAsync(ct).ConfigureAwait(false);
        if (store.NoticeById(pair.PairId, notice.NoticeId!) is { Closed: false })
            throw new IOException("Storage verification remains pending. Correct the reported cause and Retry; the item is unchanged.");
    }
}
