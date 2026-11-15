namespace Massdex.SyncShark.V2;

internal static partial class ExecutorChecks
{
    private static async Task NewestWins_ChangedLocalTargetSkippedBeforeOverwrite()
    {
        var (store, a, b, executor) = Rig();
        var observed = DateTimeOffset.UtcNow;
        var source = a.Seed("x", "planned newer winner", versionId: "winner-v1", mtime: observed.AddMinutes(-1));
        var loser = b.Seed("x", "planned older loser", versionId: "loser-v1", mtime: observed.AddMinutes(-2));
        var action = new SyncAction("newest-wins-update", 4, ActionKind.Update, Side.B, "x", null,
            source, loser, true, "newest-key");
        var edited = b.Seed("x", "edit after preview", versionId: "loser-v2", mtime: observed);
        // LocalWriter checks the victim before creating a new archive. Model that rejection, not
        // FakeEndpoint's unconditional archive. Preserves counts attempts, not successful archives.
        var writer = new RecordingWriter(b) { PreserveFailsMismatch = true };
        var results = await Apply(executor, Plan(action), a, writer);
        // Execution retains the provider's conditional rejection reason; Superseded is the
        // separate observe-only reconciliation classification, not this execution path.
        Require(results.Single().Outcome == Outcome.Skipped &&
            results.Single().Reason == "RecordingWriter: victim 'x' changed",
            "newest-wins update is skipped on the local archive's exact victim precondition failure");
        Require((await b.ObserveAsync("x", CancellationToken.None))!.Hash == edited.Hash &&
            writer.Preserves == 1 && writer.Puts == 0,
            "changed losing content is untouched: one rejected archive attempt and no overwrite attempt");
        Require(store.UnresolvedIntents("p1").Count == 0 && store.Recoveries("p1").Count == 0,
            "superseded newest-wins intent closes without a false recovery record");
    }
}
