namespace Massdex.SyncShark.V2;

/// <summary>
/// F3/F5 checks: an explicit retirement of a tagged conflict successor (re-inclusion, exclusion, ownership boundary,
/// link or stub) marks SuppressReconciledBaseline, so a cold store retires the intent and closes its dedicated
/// Retry-only notice in the same result transaction. An ordinary skip without that authority is still coerced back to
/// Failed, retaining the intent and its notice. No live pair or state.db is used.
/// </summary>
internal static class ConflictRetirementChecks
{
    private const string PairId = "p1";
    private static readonly V2Settings Settings = V2Settings.Default;

    public static Task RunAsync()
    {
        var root = LocalChecks.FreshRoot("conflict-retirement");
        var pair = new PairDef(PairId, PairId,
            new RootDef("ra", SyncProviders.Local, null, @"C:\a", "id:ra"),
            new RootDef("rb", SyncProviders.Dropbox, "acc", "/b", "id:rb"),
            [], [], true);
        var toB = Successor("copy-to-b.txt", Side.B);
        var toA = Successor("copy-to-a.txt", Side.A);
        long gen;
        using (var store = SqliteStateStore.Open(root, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            store.SavePair(pair);
            store.MarkFirstSyncDone(PairId);
            gen = store.BeginGeneration(pair.A.RootId, "test");
            store.RecordIntent(PairId, toB);
            store.RecordIntent(PairId, toA);
            store.AddNotices([Blocked(toB), Blocked(toA)]);
            Require(store.UnresolvedIntents(PairId).Count == 2 && Open(store).Length == 2,
                "two tagged successors start with their dedicated Retry-only notices");
        }

        using (var reopened = SqliteStateStore.Open(root, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash))
        {
            Require(reopened.UnresolvedIntents(PairId).Count == 2 && Open(reopened).Length == 2,
                "the intents and their notices survive a cold reopen");
            var intents = reopened.UnresolvedIntents(PairId)
                .OrderBy(intent => intent.Path, StringComparer.Ordinal).ToArray();
            // Without explicit retirement authority the store still refuses to release the frozen conflict.
            reopened.CommitResult(PairId, gen, intents[0], Skipped(intents[0], "not an explicit retirement"));
            Require(reopened.UnresolvedIntents(PairId).Any(intent => intent.ActionId == intents[0].ActionId),
                "an unsuppressed skip is coerced back to Failed and keeps the intent");
            Require(Open(reopened).Any(notice => notice.Path == intents[0].Path),
                "an unsuppressed skip leaves the dedicated notice open");
            // An explicit retirement retires the intent and closes its notice in the same result transaction.
            foreach (var intent in intents)
                reopened.CommitResult(PairId, gen, intent with { SuppressReconciledBaseline = true },
                    Skipped(intent, "explicit retirement"));
            Require(reopened.UnresolvedIntents(PairId).Count == 0,
                "explicit retirement removes every tagged successor intent");
            Require(Open(reopened).Length == 0,
                "each dedicated notice closes with its retirement in the same result transaction");
        }

        using (var reopened = SqliteStateStore.Open(root, Settings, TimeProvider.System, LocalItems.CaptureOptionalHash))
            Require(reopened.UnresolvedIntents(PairId).Count == 0 && Open(reopened).Length == 0,
                "retirement and notice closure stay durable across another cold reopen");

        Console.WriteLine("Conflict retirement checks passed: cold reopen, coercion, explicit retirement and notice closure.");
        return Task.CompletedTask;
    }

    private static SyncAction Successor(string path, Side target) =>
        new("keepboth-" + target.ToString().ToLowerInvariant(), 27, ActionKind.KeepBoth, target, path, null,
            new ItemState("file.txt", ItemKind.File, "id:source", "v1", "hash", 4,
                DateTimeOffset.UnixEpoch, ItemFlags.None),
            null, false, "split-" + target)
        { ConflictTransitionKey = "transition-key" };

    private static Notice Blocked(SyncAction successor) =>
        new(PairId, successor.Rule, NoticeKind.Info, successor.Path,
            "The keep-both copy could not be placed yet: the name is taken. Both original versions remain protected.",
            ExecutionReasons.ConflictCopyBlocked, ExecutionReasons.ConflictCopyNoticeId(PairId, successor));

    private static ActionResult Skipped(SyncAction intent, string reason) =>
        new(intent.ActionId, Outcome.Skipped, null, null, null, reason);

    private static Notice[] Open(IStateStore store) => store.OpenNotices(PairId)
        .Where(notice => notice.ReasonKey == ExecutionReasons.ConflictCopyBlocked).ToArray();

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        Console.WriteLine("PASS " + label);
    }
}
