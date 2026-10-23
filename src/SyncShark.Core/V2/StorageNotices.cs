namespace Massdex.SyncShark.V2;

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

    internal static string ReceiptScope(string? id, string? revision, string? hash) =>
        "receipt" + ShortId.Hex16(id ?? "", revision ?? "", hash ?? "");
    private static string Family(StorageNotice item) => "storage:" + item.Operation + ":" +
        ShortId.Make("item", item.RootId, item.ObjectId, item.RemoteBinding) + ":";
    internal static string Prefix(StorageNotice item) => Family(item) + (item.Scope is null ? "" : item.Scope + ":");
    /// <summary>Every item family of one operation in one pair, before the entity hash. Used by pair-wide
    /// aggregation; the matching end is <see cref="OperationEnd"/> (a ':' sorts after the '-' that starts
    /// the aggregate operation name, so the pair summary stays outside this range).</summary>
    internal static string OperationPrefix(string operation) => "storage:" + operation + ":";
    internal static string OperationEnd(string operation) => "storage:" + operation + ";";

    internal static void Report(IStateStore store, PairDef pair, string path, StorageNotice item, Exception error)
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
        var reason = Prefix(item) + FailureCauseIdentity.Of(error);
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
        var notice = new Notice(pair.PairId, 42, NoticeKind.Info, path, detail, reason, Storage: item, CauseKey: FailureCauseIdentity.Of(error));
        if (item.Operation == PointerVerificationOperation)
        {
            // Above the limit one pair summary replaces every per-file notice. Below it the item notice is
            // written as before, so Retry still applies to that exact entity.
            var summary = PointerVerificationSummary(item);
            store.RecordPointerVerificationNotice(pair.PairId, notice, Family(item),
                new Notice(pair.PairId, 42, NoticeKind.Info, string.Empty, PointerVerificationSummaryDetail,
                    Prefix(summary) + FailureCauseIdentity.Of(error), Storage: summary, CauseKey: FailureCauseIdentity.Of(error)), Family(summary));
            return;
        }
        store.ReportStorageNotice(pair.PairId, notice, Family(item));
    }

    private static StorageNotice PointerVerificationSummary(StorageNotice item) =>
        new(item.RootId, string.Empty, item.RemoteBinding, PointerVerificationPairOperation);

    private static readonly string PointerVerificationSummaryDetail =
        $"More than {NoticeAggregateLimit} online-only files changed locally and await verification. Retry requests " +
        "verification for every affected file without discarding edits; each file keeps its durable retry debt. " +
        "Ignore leaves the files protected and keeps retrying verification silently. The summary clears once every " +
        "affected file is verified.";

    /// <summary>True for a storage notice: its choice is applied by the storage notice dispatcher, never by the run planner.
    /// Shared by the host and CLI dispatch so the predicate cannot drift between them.</summary>
    internal static bool Handles(Notice notice) => notice.Storage is not null;
    internal static bool HandlesAtPath(Notice notice, string relativePath) =>
        notice.Storage is not null && PairPath.Same(notice.Path, relativePath);

    // A successful current item observation closes obsolete revisions/causes as well.
    internal static void Complete(IStateStore store, string pairId, StorageNotice item)
    {
        store.CloseNoticeFamily(pairId, Family(item));
        // Verification debt falls per entity, so the pair summary retires only once every debt is settled; any
        // still-failing entity keeps it standing rather than making unresolved failures disappear.
        if (item.Operation == PointerVerificationOperation)
            store.RetirePointerVerificationSummary(pairId, PointerVerificationSummary(item));
    }
    /// <summary>Closes the pair cause family for the requested operation. Hydration and the durable offline lane
    /// keep their own families; settlement/remote-update failures share the status-only "availability" family.</summary>
    internal static void CompletePair(IStateStore store, PairDef pair, string operation)
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
    internal static void CompleteHydrationPair(IStateStore store, PairDef pair) =>
        store.CloseStorageNoticeBatch(pair.PairId, [new(pair.A.RootId, "", pair.B.RootId, HydrationPairOperation)]);

    /// <summary>A positive observation retires a recorded pair cause only when every open pair cause is
    /// connectivity (network/auth/provider) and, for a normal lane completion, no pair cause was reported in
    /// this same turn. A binding/registration/volume/backlog cause always survives an unrelated item success.
    /// An explicit success is positive connectivity evidence even within that turn.</summary>
    internal static void CompleteSuccess(IStateStore store, PairDef pair, bool requested, bool pairCauseReportedThisTurn)
    {
        var pairNotices = store.OpenStoragePairCauses(pair.PairId, pair.A.RootId, pair.B.RootId);
        if (pairNotices.Count == 0) return;
        // Every live scope counts, including a suppressed (ignored) cause: an unrelated success must never
        // retire a binding/volume suppression, and only an all-connectivity success reaches the close below.
        if (!pairNotices.All(notice => notice.Scope is "network" or "auth" or "provider")) return;
        if (pairCauseReportedThisTurn && !requested) return;
        store.ClearStorageRootBackoff(pair.A.RootId);
        foreach (var operation in pairNotices.Select(notice => notice.Operation).Distinct())
            CompletePair(store, pair, operation == HydrationPairOperation ? OfflineOperation : "availability");
    }

}
