namespace Massdex.SyncShark.V2;

/// <summary>Provider checkpoints use the existing intent JSON and survive reopening without a schema change.</summary>
internal static partial class StorageChecks
{
    private static void ProviderCheckpoint_ReopenUncertainDone(string root)
    {
        var folder = Fresh(root, "provider-checkpoint");
        var source = F("checkpoint.txt", "id:source", "77");
        var action = new SyncAction("checkpoint-action", 18, ActionKind.CopyNew, Side.B,
            source.Path, null, source, null, false, "checkpoint-key");
        var checkpointed = action with
        {
            ProviderEffect = new ProviderEffectCheckpoint("upload-session", "opaque-session-evidence", T0)
        };
        long gen;
        using (var store = SqliteStateStore.Open(folder, Fast, new TestClock()))
        {
            store.SavePair(Pair("p1"));
            gen = store.BeginGeneration("ra", "run");
            store.RecordIntents("p1", [action]);
            Require(store.UnresolvedIntents("p1").Single().ProviderEffect is null,
                "an ordinary intent needs no provider checkpoint");
            store.RecordIntents("p1", [checkpointed]);
        }

        using (var reopened = SqliteStateStore.Open(folder, Fast, new TestClock()))
        {
            var persisted = reopened.UnresolvedIntents("p1").Single();
            Require(persisted == checkpointed,
                "RecordIntents updates persist the complete provider checkpoint across reopen on the same schema");
            reopened.CommitResult("p1", gen, persisted,
                new ActionResult(persisted.ActionId, Outcome.Uncertain, null, null, null, "commit not yet observed"));
            Require(reopened.UnresolvedIntents("p1").Single() == checkpointed,
                "an uncertain result retains its provider checkpoint");
        }

        using (var reopened = SqliteStateStore.Open(folder, Fast, new TestClock()))
        {
            var persisted = reopened.UnresolvedIntents("p1").Single();
            Require(persisted.ProviderEffect == checkpointed.ProviderEffect,
                "an uncertain checkpoint survives a second reopen");
            var target = source with { ObjectId = "id:target", VersionId = "target-revision" };
            reopened.CommitResult("p1", gen, persisted,
                new ActionResult(persisted.ActionId, Outcome.Done, source, target, null, null));
            Require(reopened.UnresolvedIntents("p1").Count == 0,
                "a done result closes the checkpointed intent");
        }

        using (var reopened = SqliteStateStore.Open(folder, Fast, new TestClock()))
            Require(reopened.UnresolvedIntents("p1").Count == 0 && reopened.Baselines("p1").Count == 1,
                "the resolved intent stays closed and its baseline survives reopen");
    }
}
