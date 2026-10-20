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

    internal static string ReceiptScope(PilotRemoteEntry remote) =>
        "receipt" + ShortId.Hex16(remote.Id ?? "", remote.Revision ?? "", remote.ContentHash ?? "");
    private static string Family(StorageNotice item) => "storage:" + item.Operation + ":" +
        ShortId.Make("item", item.RootId, item.ObjectId, item.RemoteBinding) + ":";
    internal static string Prefix(StorageNotice item) => Family(item) + (item.Scope is null ? "" : item.Scope + ":");

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
        var reason = Prefix(item) + "cause" + ShortId.Hex16(error.GetType().Name,
            error.HResult.ToString(System.Globalization.CultureInfo.InvariantCulture), error.Message);
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
        store.ReportStorageNotice(pair.PairId, new Notice(pair.PairId, 42, NoticeKind.Info, path, detail, reason,
            Storage: item), Family(item));
    }

    /// <summary>True for a storage notice: its choice is applied by <see cref="ApplyAsync"/>, never by the run planner.
    /// Shared by the host and CLI dispatch so the predicate cannot drift between them.</summary>
    internal static bool Handles(Notice notice) => notice.Storage is not null;
    internal static bool HandlesAtPath(Notice notice, string relativePath) =>
        notice.Storage is not null && PairPath.Same(notice.Path, relativePath);

    // A successful current item observation closes obsolete revisions/causes as well.
    internal static void Complete(SqliteStateStore store, string pairId, StorageNotice item) =>
        store.CloseNoticeFamily(pairId, Family(item));
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
            var inactivePair = !pair.Enabled || store.PairPaused(pair.PairId) || (paused?.Invoke(pair) ?? false);
            string message;
            if (queued)
                message = inactivePair
                    ? "Retry queued; it will run when the pair resumes. Start the background app with autosync start, or open the app."
                    : "Retry queued; the background app will retry it now. If it is not running, use autosync start or open the app.";
            else if (item.ObjectId.Length == 0)
            {
                store.ClearStorageRootBackoff(item.RootId);
                message = "The pair retry delay is cleared. Start the background app with autosync start, or open the app; " +
                    "pending work retries on its normal schedule. Retry this notice in the app to verify the pair immediately.";
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
