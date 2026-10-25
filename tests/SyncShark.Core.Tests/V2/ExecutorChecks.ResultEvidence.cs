namespace Massdex.SyncShark.V2;

internal static partial class ExecutorChecks
{
    private static async Task CommitOnlyMissingSuccessorsAsync()
    {
        var (store, a, b, executor) = Rig();
        var left = a.Seed("file", "old");
        var right = b.Seed("file", "old");
        SeedBaseline(store, "linked", left, right);
        var forget = Act(ActionKind.Forget, Side.B, "file", key: "linked", rule: 27);
        var target = new RecordingWriter(b);
        var successor = Act(ActionKind.KeepBoth, Side.B, "copy", source: left, key: "copy", rule: 27) with
            { ConflictTransitionKey = "linked" };
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var results = await Apply(executor, attempt == 0 ? Plan(forget) : Plan(forget, successor), a, target);
            var result = results.Single(item => item.ActionId == forget.ActionId);
            Require(result.Outcome == Outcome.Failed && result.Reason == ExecutionReasons.MissingConflictSuccessors.Detail,
                "missing successors fail explicitly before any effect");
        }
        Require(store.Baselines("p1").Single().ObjectKey == "linked" && target.Calls.Count == 0,
            "failed Forget retains correspondence and never calls the endpoint");
        Require(store.OpenNotices("p1").Count(n => n.Path == forget.Path) == 1 &&
            store.UnresolvedIntents("p1").Count(intent => intent.ActionId == forget.ActionId) == 1,
            "repeated failure has one notice and durable retry intent");
        var resolved = (await executor.ReconcileAsync([forget], 2, a, b, default)).Single();
        Require(resolved.Outcome == Outcome.Skipped && !store.UnresolvedIntents("p1").Any(intent => intent.ActionId == forget.ActionId) &&
            store.Baselines("p1").Count == 1, "commit-only intent settles not-applied for replanning, never observation uncertainty");
        foreach (var kind in new[] { ActionKind.Adopt, ActionKind.Forget, ActionKind.Hold })
            Require(ResultEvidence.Normalize(forget with { Kind = kind },
                new(forget.ActionId, Outcome.Uncertain, null, null, null, "failure")).Outcome == Outcome.Failed,
                "every commit-only kind rejects Uncertain");
    }

    private static async Task ReconcileMoveUnhashedSourceAsync()
    {
        var (store, a, b, executor) = Rig();
        var source = a.Seed("new", "content") with { Hash = null };
        b.Seed("old", "content");
        var move = await ApplyMoveAsync(b, "old", "new", source, "linked");
        store.RecordIntent("p1", move);
        var observed = await EffectResolver.ResolveAsync(move, b, a, default,
            isStateDamage: StorageFailureClassifier.IsStateDamage);
        Require(observed.Outcome == Outcome.Done && observed.NewA?.Hash is null,
            "identity-verified move can produce Done with an unhashed opposite source");
        var unavailable = new RecordingWriter(a) { ObserveFails = true };
        var result = (await executor.ReconcileAsync([move], 2, unavailable, b, default)).Single();
        Require(result.Outcome == Outcome.Failed && store.Baselines("p1").Count == 0 &&
            store.OpenNotices("p1").Count == 1, "unhashed Done becomes one failed unsynced item with a notice");
        var retry = (await executor.ReconcileAsync([move], 3, a, b, default)).Single();
        Require(retry.Outcome == Outcome.Done && store.Baselines("p1").Count == 1 &&
            store.UnresolvedIntents("p1").Count == 0, "fresh source hash resolves the same frozen move without replay");
    }

    private static async Task DeleteSubmitEscalationAsync()
    {
        var (store, a, b, executor) = Rig();
        var victim = b.Seed("victim", "old");
        var action = Act(ActionKind.Delete, Side.B, "victim", expected: victim) with
            { ProviderEffect = new("delete-submit", "submission", DateTimeOffset.UnixEpoch) };
        await b.DeleteAsync(action, default);
        store.RecordIntent("p1", action);
        for (var attempt = 0; attempt < 2; attempt++)
            Require((await executor.ReconcileAsync([action], 1, a, b, default)).Single().Outcome == Outcome.Uncertain,
                "unproven absent submission remains protected");
        Require(store.OpenNotices("p1").Count(n => n.ReasonKey == ExecutionReasons.StuckProviderJob.Key) == 1,
            "repeated stuck delete submission raises exactly one escalation");
        b.Seed("victim", "replacement");
        Require((await executor.ReconcileAsync([action], 1, a, b, default)).Single().Outcome == Outcome.Skipped &&
            !store.OpenNotices("p1").Any(n => n.ReasonKey == ExecutionReasons.StuckProviderJob.Key),
            "settled submission closes its escalation without deleting the replacement");
    }
}
