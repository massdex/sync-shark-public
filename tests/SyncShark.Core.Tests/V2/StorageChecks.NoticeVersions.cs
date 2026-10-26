namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    // RQ-038/050/060: retained versions remain durable, action-specific and pair-scoped across replay.
    private static void NoticeVersionChecks(string root)
    {
        foreach (var side in new[] { Side.A, Side.B })
        {
            var folder = Fresh(root, "notice-versions-" + side);
            var loser = F("same.txt", "loser-id", "11") with { VersionId = "loser-rev" };
            var source = F("same.txt", "source-id", "22") with { VersionId = "source-rev" };
            var target = F("same.txt", "target-id", "22") with { VersionId = "committed-target-rev" };
            var action = new SyncAction("original-update", 4, ActionKind.Update, side, "same.txt", null,
                source, loser, true, "link");
            var notice = new Notice("p1", 4, NoticeKind.Info, "same.txt", "Older version retained", "newer-wins",
                RetainedVersion: new RetainedNoticeVersion(action.ActionId, side, loser, source));
            var nextNotice = notice with { RetainedVersion = notice.RetainedVersion! with { ActionId = "next-update" } };
            var otherNotice = notice with { PairId = "p2" };
            string noticeId;
            string otherId;
            var recovery = new RecoveryRef(side, "retained/original", loser.VersionId, loser.Hash!, loser.Size);
            using (var store = SqliteStateStore.Open(folder, Fast, new TestClock(), LocalItems.CaptureOptionalHash))
            {
                store.SavePair(Pair("p1"));
                store.SavePair(Pair("p2"));
                store.AddNotices([notice, nextNotice, otherNotice]);
                var rows = store.OpenNotices("p1");
                Require(rows.Count == 2 && rows.Select(row => row.NoticeId).Distinct().Count() == 2,
                    "same-path retained notices for distinct actions have distinct identities");
                noticeId = rows.Single(row => row.RetainedVersion!.ActionId == action.ActionId).NoticeId!;
                otherId = store.OpenNotices("p2").Single().NoticeId!;
                Require(noticeId != otherId && store.NoticeById("p2", noticeId) is null &&
                    store.NoticeById("missing", noticeId) is null, "notice lookup isolates pair identity");
                store.RecordIntent("p1", action);
                Require(store.IntentById("p1", action.ActionId) == action &&
                    store.IntentById("p2", action.ActionId) is null, "indexed intent lookup isolates the action's pair");
                store.CommitResult("p1", 1, action,
                    new ActionResult(action.ActionId, Outcome.Failed, null, null, recovery, "retry"));
            }
            using (var store = SqliteStateStore.Open(folder, Fast, new TestClock(), LocalItems.CaptureOptionalHash))
            {
                var retained = store.NoticeById("p1", noticeId);
                Require(retained is { Closed: false } && retained.Value.Notice.Detail == notice.Detail &&
                    retained.Value.Notice.RetainedVersion == notice.RetainedVersion &&
                    store.IntentById("p1", action.ActionId) == action,
                    "failed update preserves readable retained metadata and durable intent after reopen");
                var savedRecovery = store.RecoveryForAction("p1", action.ActionId);
                Require(savedRecovery is not null && savedRecovery.Location == recovery.Location &&
                    savedRecovery.VersionId == loser.VersionId && savedRecovery.Hash == loser.Hash &&
                    savedRecovery.Side == side && store.RecoveryForAction("p2", action.ActionId) is null &&
                    store.RecoveryForAction("p1", "missing") is null,
                    "indexed Recovery lookup round-trips retained bytes and does not cross pairs");
                store.CommitResult("p1", 2, action, new ActionResult(action.ActionId, Outcome.Done,
                    side == Side.A ? target : null, side == Side.B ? target : null, recovery, null));
                store.AddNotices([notice]); // Replay stale planned source stamp after enrichment.
                Require(store.IntentById("p1", action.ActionId) is null &&
                    store.NoticeById("p1", noticeId)!.Value.Notice.RetainedVersion!.Winner == target,
                    "Done removes intent and persists actual target winner; notice replay cannot overwrite enrichment");
            }
            using (var store = SqliteStateStore.Open(folder, Fast, new TestClock(), LocalItems.CaptureOptionalHash))
            {
                Require(store.NoticeById("p1", noticeId)!.Value.Notice.RetainedVersion ==
                    (notice.RetainedVersion! with { Winner = target }), "committed target stamp survives SQLite reopen");
                var restore = action with { ActionId = "restore-version", Source = loser, ExpectedTarget = target,
                    ResolvedNoticeId = noticeId, SuppressReconciledBaseline = true };
                store.RecordIntent("p1", restore);
                foreach (var outcome in new[] { Outcome.Failed, Outcome.Uncertain })
                {
                    store.CommitResult("p1", 3, restore, new ActionResult(restore.ActionId, outcome, null, null, null, "retry"));
                    Require(store.NoticeById("p1", noticeId) is { Closed: false } &&
                        store.IntentById("p1", restore.ActionId)?.ResolvedNoticeId == noticeId,
                        "failed or uncertain restoration retains the open notice and durable resolution target");
                }
                var refused = restore with { ActionId = "restore-refused" };
                store.RecordIntent("p1", refused);
                store.CommitResult("p1", 4, refused, new ActionResult(refused.ActionId, Outcome.Done,
                    null, null, null, null) { ProviderNameRefused = true });
                Require(store.NoticeById("p1", noticeId) is { Closed: false },
                    "provider name refusal never closes the notice even when result outcome says Done");
                // A successfully committed action in another pair cannot close this pair's notice.
                var foreign = restore with { ActionId = "restore-foreign", ResolvedNoticeId = otherId };
                store.RecordIntent("p1", foreign);
                store.CommitResult("p1", 5, foreign, new ActionResult(foreign.ActionId, Outcome.Done,
                    side == Side.A ? loser : null, side == Side.B ? loser : null, null, null));
                Require(store.NoticeById("p2", otherId) is { Closed: false },
                    "Done restoration cannot close a notice belonging to another pair");
                store.CommitResult("p1", 6, restore, new ActionResult(restore.ActionId, Outcome.Done,
                    side == Side.A ? loser : null, side == Side.B ? loser : null,
                    new RecoveryRef(side, "retained/restore", target.VersionId, target.Hash!, target.Size), null));
                Require(store.NoticeById("p1", noticeId) is { Closed: true } &&
                    store.IntentById("p1", restore.ActionId) is null &&
                    store.Baselines("p1").Single().A.Hash == target.Hash,
                    "Done atomically resolves intent and notice despite suppressed baseline publication");
                store.AddNotices([notice, nextNotice]);
            }
            using (var store = SqliteStateStore.Open(folder, Fast, new TestClock(), LocalItems.CaptureOptionalHash))
            {
                var closed = store.NoticeById("p1", noticeId);
                var successor = store.OpenNotices("p1").Single(row => row.RetainedVersion?.ActionId == "restore-version");
                Require(successor.RetainedVersion == new RetainedNoticeVersion("restore-version", side, target, loser) &&
                    successor.NoticeId != noticeId &&
                    store.RecoveryForAction("p1", "restore-version")?.Location == "retained/restore",
                    "successful restore durably publishes a successor retaining displaced target and actual restored winner");
                Require(closed is { Closed: true } && closed.Value.Notice.RetainedVersion!.Winner == target &&
                    store.OpenNotices("p1").Where(row => row.RetainedVersion is not null)
                        .Select(row => row.RetainedVersion!.ActionId).ToHashSet(StringComparer.Ordinal)
                        .SetEquals(["next-update", "restore-version"]) &&
                    store.NoticeById("p2", otherId) is { Closed: false },
                    "closed retained notice stays closed and enriched across replay/reopen; other versions and pairs remain open");
            }
        }
        Console.WriteLine("PASS Storage_NoticeVersions_ReopenIsolationAndCommit");
    }
}
