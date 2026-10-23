using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;

namespace Massdex.SyncShark.V2;

public sealed record StorageNotice(string RootId, string ObjectId, string RemoteBinding, string Operation, string? Scope = null);

internal sealed class StoragePairException(string cause, string message, Exception? inner = null) : IOException(message, inner)
{
    internal string Cause { get; } = cause;
}

/// <summary>Shared storage notice vocabulary and actions; no delete or upload authority.</summary>
internal static class StorageNotices
{
    /// <summary>Pair-scoped hydration family, separate from settlement's "availability" family so a successful
    /// hydration clears only its own cause and never hides a blocked-registration or volume problem.</summary>
    internal const string HydrationPairOperation = "hydration-pair";
    /// <summary>The durable offline hydration lane. Its pair-level causes share the one hydration-pair family with
    /// the callback lane, so a pair outage produces exactly one notice whichever lane observed it first.</summary>
    internal const string OfflineOperation = "offline";
    /// <summary>Per-item online-only content verification debt. Its family stays item/entity keyed.</summary>
    internal const string PointerVerificationOperation = "pointer-verify";
    /// <summary>One pair-level summary replacing per-file verification notices above the limit.</summary>
    internal const string PointerVerificationPairOperation = "pointer-verify-pair";

    /// <summary>Above this many matching items a pair gets one summary instead of per-file notices. Durable
    /// per-entity debts (rehash flags, sync-retry rows and hydration jobs) are unchanged.</summary>
    internal const int NoticeAggregateLimit = 20;

    internal static string ReceiptScope(PilotRemoteEntry remote) =>
        "receipt" + ShortId.Hex16(remote.Id ?? "", remote.Revision ?? "", remote.ContentHash ?? "");
    private static string Family(StorageNotice item) => "storage:" + item.Operation + ":" +
        ShortId.Make("item", item.RootId, item.ObjectId, item.RemoteBinding) + ":";
    internal static string Prefix(StorageNotice item) => Family(item) + (item.Scope is null ? "" : item.Scope + ":");
    /// <summary>Every item family of one operation in one pair, before the entity hash. Used by pair-wide
    /// aggregation; the matching end is <see cref="OperationEnd"/> (a ':' sorts after the '-' that starts
    /// the aggregate operation name, so the pair summary stays outside this range).</summary>
    internal static string OperationPrefix(string operation) => "storage:" + operation + ":";
    internal static string OperationEnd(string operation) => "storage:" + operation + ";";

    internal static void Report(SqliteStateStore store, PairDef pair, string path, StorageNotice item, Exception error)
    {
        if (error is StoragePairException pairError)
        {
            // A pair-level cause is ONE notice for the pair, never one per file. Hydration keeps its own family;
            // every other pair-level cause keeps the shared "availability" family.
            var operation = item.Operation switch
            {
                "hydration" or HydrationPairOperation or OfflineOperation => HydrationPairOperation,
                _ => "availability"
            };
            item = item with { ObjectId = "", Operation = operation, Scope = pairError.Cause };
            path = "";
        }
        var reason = Prefix(item) + Cause(error);
        var pairScoped = item.Operation == HydrationPairOperation;
        var label = item.Operation == HydrationPairOperation ? "hydration" : item.Operation;
        var detail = "Cloud storage " + label + " failed: " + error.Message +
            (pairScoped
                ? ""
                : item.Operation == "hydration"
                    ? " Retry the read after correcting the cause, or Ignore this cause."
                    : " Correct the cause and Retry verification/settlement, or Ignore this cause. Local bytes are retained and no upload is repeated.");
        // One open notice per item+operation: the store updates the active family notice in place (keeping its
        // stable id) for a new cause, returns null for an ignored exact cause, and never reopens ignored history.
        var notice = new Notice(pair.PairId, 42, NoticeKind.Info, path, detail, reason, Storage: item);
        if (item.Operation == PointerVerificationOperation)
        {
            // Above the limit one pair summary replaces every per-file notice. Below it the item notice is
            // written as before, so Retry still applies to that exact entity.
            var summary = PointerVerificationSummary(item);
            store.RecordPointerVerificationNotice(pair.PairId, notice, Family(item),
                new Notice(pair.PairId, 42, NoticeKind.Info, string.Empty, PointerVerificationSummaryDetail,
                    Prefix(summary) + Cause(error), Storage: summary), Family(summary));
            return;
        }
        store.ReportStorageNotice(pair.PairId, notice, Family(item));
    }

    private static string Cause(Exception error) => "cause" + ShortId.Hex16(error.GetType().Name,
        error.HResult.ToString(System.Globalization.CultureInfo.InvariantCulture), error.Message);

    private static StorageNotice PointerVerificationSummary(StorageNotice item) =>
        new(item.RootId, string.Empty, item.RemoteBinding, PointerVerificationPairOperation);

    private static readonly string PointerVerificationSummaryDetail =
        $"More than {NoticeAggregateLimit} online-only files changed locally and await verification. Retry requests " +
        "verification for every affected file without discarding edits; each file keeps its durable retry debt. " +
        "Ignore leaves the files protected and keeps retrying verification silently. The summary clears once every " +
        "affected file is verified.";

    /// <summary>True for a storage notice: its choice is applied by <see cref="ApplyAsync"/>, never by the run planner.
    /// Shared by the host and CLI dispatch so the predicate cannot drift between them.</summary>
    internal static bool Handles(Notice notice) => notice.Storage is not null;
    internal static bool HandlesAtPath(Notice notice, string relativePath) =>
        notice.Storage is not null && PairPath.Same(notice.Path, relativePath);

    // A successful current item observation closes obsolete revisions/causes as well.
    internal static void Complete(SqliteStateStore store, string pairId, StorageNotice item)
    {
        store.CloseNoticeFamily(pairId, Family(item));
        // Verification debt falls per entity, so the pair summary retires only once every debt is settled; any
        // still-failing entity keeps it standing rather than making unresolved failures disappear.
        if (item.Operation == PointerVerificationOperation)
            store.RetirePointerVerificationSummary(pairId, PointerVerificationSummary(item));
    }
    /// <summary>Closes the pair cause family for the requested operation. Hydration and the durable offline lane
    /// keep their own families; settlement/remote-update failures share the status-only "availability" family.</summary>
    internal static void CompletePair(SqliteStateStore store, PairDef pair, string operation)
    {
        var family = operation switch
        {
            "hydration" or HydrationPairOperation or OfflineOperation => HydrationPairOperation,
            _ => "availability"
        };
        Complete(store, pair.PairId, new(pair.A.RootId, "", pair.B.RootId, family));
    }

    /// <summary>Closes the pair's hydration cause family only: one bounded indexed update, prior-process notices
    /// included. Ignored rows keep their choice, so cause-specific suppression survives.</summary>
    internal static void CompleteHydrationPair(SqliteStateStore store, PairDef pair) =>
        store.CloseStorageNoticeBatch(pair.PairId, [new(pair.A.RootId, "", pair.B.RootId, HydrationPairOperation)]);

    /// <summary>A positive observation retires a recorded pair cause only when every open pair cause is
    /// connectivity (network/auth/provider) and, for a normal lane completion, no pair cause was reported in
    /// this same turn. A binding/registration/volume/backlog cause always survives an unrelated item success.
    /// An explicit success is positive connectivity evidence even within that turn.</summary>
    internal static void CompleteSuccess(SqliteStateStore store, PairDef pair, bool requested, bool pairCauseReportedThisTurn)
    {
        var pairNotices = store.OpenStoragePairCauses(pair.PairId, pair.A.RootId, pair.B.RootId);
        if (pairNotices.Count == 0) return;
        if (!pairNotices.All(notice => notice.Scope is "network" or "auth" or "provider")) return;
        if (pairCauseReportedThisTurn && !requested) return;
        store.ClearStorageRootBackoff(pair.A.RootId);
        foreach (var operation in pairNotices.Select(notice => notice.Operation).Distinct())
            CompletePair(store, pair, operation == HydrationPairOperation ? OfflineOperation : "availability");
    }

    /// <summary>
    /// Positive-evidence classification shared by every storage lane (hydration, its durable offline lane, settlement
    /// and pointer refresh). A pair-level cause (network, account auth or a disconnected provider) fails every file,
    /// so it is reported as ONE pair notice; anything item-specific (a missing revision/hash, a changed remote
    /// revision, a range/disk fault) stays a per-item notice. Never infers from message text, and a local
    /// cancellation is not a failure to report.
    /// </summary>
    internal static StoragePairException? ClassifyFailure(Exception error)
    {
        foreach (var current in Chain(error))
        {
            switch (current)
            {
                case StoragePairException pair:
                    return pair;
                case EndpointAccessException access:
                    // Created only by DropboxItems.MapAccessError from IsAccessRefusal (401, OAuth invalid_grant or a
                    // missing protected credential): an account-access refusal, not an item permission.
                    return new StoragePairException("auth", AuthDetail, access);
                case DropboxCredentialUnavailableException credential:
                    return new StoragePairException("auth", AuthDetail, credential);
                case DropboxApiException api when api.OAuthError == DropboxOAuthError.InvalidGrant:
                    return new StoragePairException("auth", AuthDetail, api);
                case DropboxApiException api when api.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    return new StoragePairException("auth", AuthDetail, api);
                case HttpRequestException http when http.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    return new StoragePairException("auth", AuthDetail, http);
                case SyncRunRefusedException refusal:
                    // The connection is missing, signed out or not connected: every file under the pair fails.
                    return new StoragePairException("provider", ProviderDetail, refusal);
                case SocketException socket:
                    return new StoragePairException("network", NetworkDetail, socket);
                case ProviderTransportTimeoutException timeout:
                    return new StoragePairException("network", NetworkDetail, timeout);
                case AuthenticationException tls:
                    // A TLS handshake/secure-connection failure is transport, not an account sign-in refusal.
                    return new StoragePairException("network", NetworkDetail, tls);
                case HttpRequestException httpServer when httpServer.StatusCode is null ||
                    httpServer.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
                    (int?)httpServer.StatusCode is >= 500 and <= 599:
                    return new StoragePairException("network", NetworkDetail, httpServer);
                case OperationCanceledException { InnerException: TimeoutException } cancelled:
                    return new StoragePairException("network", NetworkDetail, cancelled);
            }
        }
        return null;
    }

    private const string AuthDetail =
        "Dropbox sign-in was refused during storage work. Reconnect this Dropbox account, then Retry, or Ignore this cause.";
    private const string NetworkDetail =
        "Dropbox could not be reached during storage work. Check the network connection; the affected work stays deferred and retries, then Retry, or Ignore this cause.";
    private const string ProviderDetail =
        "The Dropbox connection is unavailable during storage work. Reconnect the provider, then Retry, or Ignore this cause.";

    private static IEnumerable<Exception> Chain(Exception error)
    {
        // Flatten at any depth: a native wrapper may carry an AggregateException as its InnerException.
        if (error is AggregateException aggregate)
        {
            foreach (var inner in aggregate.Flatten().InnerExceptions)
                foreach (var nested in Chain(inner))
                    yield return nested;
            yield break;
        }
        yield return error;
        if (error.InnerException is { } innerException)
            foreach (var nested in Chain(innerException))
                yield return nested;
    }

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
