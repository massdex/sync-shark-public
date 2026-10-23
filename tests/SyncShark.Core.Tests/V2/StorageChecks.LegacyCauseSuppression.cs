using System.Globalization;

namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    private static void LegacyCauseSuppressionChecks(string root)
    {
        var folder = Fresh(root, "legacy-cause-suppression");
        using var store = SqliteStateStore.Open(folder, Fast, TimeProvider.System, LocalItems.CaptureOptionalHash);
        var pair = Pair("p1");
        store.SavePair(pair);
        foreach (var reason in new[] { "run-unavailable", "autosync-watch-unavailable" })
            LegacyDiagnosticCauseSuppression(store, reason);
        var item = new StorageNotice(pair.A.RootId, "legacy-item", pair.B.RootId, "hydration");
        LegacyStorageCauseSuppression(store, pair, item);
        LegacyStorageCauseSuppression(store, pair, item with { ObjectId = "", Operation = "pointer-verify-pair" });
    }

    private static void LegacyDiagnosticCauseSuppression(IStateStore store, string reason)
    {
        var error = new IOException("legacy diagnostic", 1);
        var legacy = new Notice("p1", reason == "run-unavailable" ? 40 : 0, NoticeKind.Info, "", error.Message, reason,
            CauseKey: error.Message, FailureRetry: new(1, DateTimeOffset.MinValue));
        store.AddNotices([legacy]);
        var id = store.OpenNotices("p1").Single(n => n.ReasonKey == reason).NoticeId!;
        store.CloseNotice(id, NoticeChoices.Ignore);
        var current = legacy with { CauseIdentity = FailureCauseIdentity.Of(error) };
        store.AddNotices([current]);
        Require(store.OpenNotices("p1").All(n => n.ReasonKey != reason) &&
            store.NoticeById("p1", id)?.Notice.CauseIdentity == current.CauseIdentity,
            "legacy diagnostic Ignore survives identity migration: " + reason);
        store.AddNotices([current with { CauseKey = "new attempt text" }]);
        Require(store.OpenNotices("p1").All(n => n.ReasonKey != reason),
            "migrated Ignore survives later display changes: " + reason);
        store.AddNotices([current with { CauseIdentity = FailureCauseIdentity.Of(new IOException(error.Message, 2)) }]);
        Require(store.OpenNotices("p1").Single(n => n.ReasonKey == reason).NoticeId == id,
            "different stable cause still resurfaces: " + reason);
        store.CloseNotice(id, "resolved");
    }

    private static void LegacyStorageCauseSuppression(SqliteStateStore store, PairDef pair, StorageNotice item)
    {
        var error = new IOException("legacy storage diagnostic", 1);
        var oldReason = StorageNotices.Prefix(item) + "cause" + ShortId.Hex16(error.GetType().Name,
            error.HResult.ToString(CultureInfo.InvariantCulture), error.Message);
        // Legacy storage payloads had neither CauseKey nor CauseIdentity; the message hash lived in ReasonKey.
        store.AddNotices([new(pair.PairId, 42, NoticeKind.Info, "legacy.txt", error.Message, oldReason, Storage: item)]);
        var id = store.OpenNotices(pair.PairId).Single(n => n.ReasonKey == oldReason).NoticeId!;
        store.CloseNotice(id, NoticeChoices.Ignore);
        StorageNotices.Report(store, pair, "legacy.txt", item, error);
        var migrated = store.NoticeById(pair.PairId, id)!.Value.Notice;
        Require(migrated.CauseIdentity == FailureCauseIdentity.Of(error) && migrated.ReasonKey != oldReason &&
            store.OpenNotices(pair.PairId).All(n => n.Storage != item),
            "legacy storage Ignore migrates its exact cause in place without resurfacing");
        StorageNotices.Report(store, pair, "legacy.txt", item, new IOException("changed display", 1));
        Require(store.OpenNotices(pair.PairId).All(n => n.Storage != item),
            "migrated storage Ignore survives variable display text");
        StorageNotices.Report(store, pair, "legacy.txt", item, new IOException(error.Message, 2));
        Require(store.OpenNotices(pair.PairId).Single(n => n.Storage == item).NoticeId != id,
            "legacy migration does not suppress a different storage cause");
        StorageNotices.Complete(store, pair.PairId, item);
    }
}
