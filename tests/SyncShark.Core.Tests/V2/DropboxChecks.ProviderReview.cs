namespace Massdex.SyncShark.V2;

internal static partial class DropboxChecks
{
    private static async Task ProviderRetainedParentAsync(string root, bool full)
    {
        using var fx = new ProviderFollowupFixture(root);
        fx.A.Seed("P", "", ItemKind.Folder);
        fx.A.Seed("P/old", "", ItemKind.Folder);
        fx.A.Seed("P/old/unique.txt", "only cloud copy");
        fx.A.Seed("Q", "", ItemKind.Folder);
        await fx.FirstAsync();
        await fx.NextAsync(false);
        var original = await fx.B.ObserveAsync("P/old/unique.txt", CancellationToken.None);
        await ProviderRenameAsync(fx.A, "P/old", "Q/bad");
        fx.WriterB.Refused.Add("Q/bad");
        await fx.NextAsync(false);
        await ProviderDeleteAsync(fx.A, "P");
        fx.Reopen();
        fx.ClearEffects();
        var result = await fx.NextAsync(full);
        Require(await fx.B.ObserveAsync("P/old/unique.txt", CancellationToken.None) == original &&
            await fx.B.ObserveAsync("P/old", CancellationToken.None) is not null &&
            !fx.WriterB.Effects.Any(effect => effect.Kind == "delete"),
            "parent deletion cannot remove the retained old copy of a refused folder move");
        Require(fx.Store.OpenNotices("p1").Any(notice => notice.Path == "P") &&
            (result.Held > 0 || result.Skipped > 0),
            "parent deletion is held or skipped with a notice, not silently discarded");
        Require(fx.Store.OpenNotices("p1").Any(notice => notice.Detail.Contains("Dropbox-side changes", StringComparison.Ordinal)),
            "refusal notice explains that old-path Dropbox changes are paused");
        // Exercise the executor backstop with a stale delete even though the planner has already held it.
        var prior = fx.Store.OpenNotices("p1").Single(notice => notice.Path == "P" &&
            notice.ReasonKey == ProviderRefusedPaths.RetainedNoticeKey);
        var staleDelete = new SyncAction("stale-retained-parent", 31, ActionKind.Delete, Side.B, "P", null,
            null, (await fx.B.ObserveAsync("P", CancellationToken.None))!, false, null);
        var executor = new Executor(fx.Store, StorageChecks.Fast, ExecutorChecks.Admission(), isStateDamage: StorageFailureClassifier.IsStateDamage);
        var guarded = new List<ActionResult>();
        await foreach (var outcome in executor.ApplyAsync(new Plan("p1", 100, [staleDelete], []),
                           fx.WriterA, fx.WriterB, CancellationToken.None)) guarded.Add(outcome);
        Require(guarded.Count == 1 && guarded[0].Outcome == Outcome.Skipped &&
            fx.WriterB.Effects.All(effect => effect.Kind != "delete"), "executor independently blocks the stale parent delete");
        Require(fx.Store.OpenNotices("p1").Count(notice => notice.Path == "P" &&
                notice.ReasonKey == ProviderRefusedPaths.RetainedNoticeKey) == 1 &&
            fx.Store.OpenNotices("p1").Any(notice => notice.NoticeId == prior.NoticeId),
            "executor guard reuses the planner problem instead of raising a second notice under another rule");
    }

    private static async Task ProviderValidFolderRenameAsync(string root, bool moved)
    {
        using var fx = new ProviderFollowupFixture(root);
        fx.A.Seed(moved ? "old" : "bad", "", ItemKind.Folder);
        fx.A.Seed((moved ? "old" : "bad") + "/child.txt", "unique bytes");
        if (moved)
        {
            await fx.FirstAsync();
            await fx.NextAsync(false);
            await ProviderRenameAsync(fx.A, "old", "bad");
        }
        fx.WriterB.Refused.Add("bad");
        if (moved) await fx.NextAsync(false); else await fx.FirstAsync();
        var old = moved ? await fx.B.ObserveAsync("old", CancellationToken.None) : null;
        fx.Reopen();
        await fx.NextAsync(false); // persist a source cursor before the remedy
        await ProviderRenameAsync(fx.A, "bad", "valid");
        fx.ClearEffects();
        await fx.NextAsync(false);
        var valid = await fx.B.ObserveAsync("valid", CancellationToken.None);
        Require(valid is not null && (old is null || valid.ObjectId == old.ObjectId) &&
            await fx.B.ObserveAsync("valid/child.txt", CancellationToken.None) is not null &&
            await fx.B.ObserveAsync("bad", CancellationToken.None) is null &&
            await fx.B.ObserveAsync("old", CancellationToken.None) is null,
            "valid rename syncs the subtree without duplicate cloud copies");
        Require(!moved || fx.WriterB.Effects.Count(effect => effect.Kind == "move" && effect.Path == "valid") == 1,
            "valid rename retries exactly one Move of the original cloud folder");
        Require(fx.Store.OpenNotices("p1").All(notice => notice.ReasonKey != ProviderNameRefusedException.NoticeKey),
            "valid rename automatically closes the refusal notice");
    }
}
