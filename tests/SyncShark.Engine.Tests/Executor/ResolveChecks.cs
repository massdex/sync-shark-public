using Massdex.SyncShark.Engine.Executor;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Contract checks for Resolve: a stale reviewed token acts on nothing, Dismiss closes, Ignore hides and
/// Unignore restores, and the PC-name label format is supplied from composition (F065 plumbing).</summary>
public static class ResolveChecks
{
    /// <summary>Verifies the Resolve contract through the module surface.</summary>
    public static async Task RunAsync()
    {
        var pair = Scenarios.ItemChecks.Pair();
        using var store = new TestDoubles.InMemoryStateStore();
        var local = new TestDoubles.FakeSide(Side.Local);
        var dropbox = new TestDoubles.FakeSide(Side.Dropbox);
        var resolve = new Resolve();
        var notice = new Notice(5, NoticeKind.Failure, TextKey.FailChanged, "item",
            new IssueSubject([new NoticeItem(Side.Local, "item", "id")], Side.Local), DateTimeOffset.UnixEpoch, false);
        await store.SaveNoticeAsync(pair.PairId, notice, default);

        // A reviewed token for another identity must not close the stored row.
        await resolve.ApplyAsync(pair, Scenarios.ItemChecks.Settings, _ => { }, null!, notice with { Path = "other" },
            ResolutionChoice.Dismiss, null, local, dropbox, store, null!, null!, default);
        Check.Require(store.OpenNotices.Count == 1, "A stale reviewed token resolved the notice");
        await resolve.ApplyAsync(pair, Scenarios.ItemChecks.Settings, _ => { }, null!, notice, ResolutionChoice.Dismiss,
            null, local, dropbox, store, null!, null!, default);
        Check.Require(store.OpenNotices.Count == 0, "Dismiss did not close the reviewed notice");

        // Ignore hides without closing; Unignore restores it.
        var toggled = notice with { Id = 6 };
        await store.SaveNoticeAsync(pair.PairId, toggled, default);
        await resolve.ApplyAsync(pair, Scenarios.ItemChecks.Settings, _ => { }, null!, toggled, ResolutionChoice.Ignore,
            null, local, dropbox, store, null!, null!, default);
        Check.Require(store.OpenNotices.Single(n => n.Id == 6).Ignored, "Ignore did not hide the notice");
        await resolve.ApplyAsync(pair, Scenarios.ItemChecks.Settings, _ => { }, null!, toggled,
            ResolutionChoice.Unignore, null, local, dropbox, store, null!, null!, default);
        Check.Require(!store.OpenNotices.Single(n => n.Id == 6).Ignored, "Unignore did not restore the notice");

        // F065: the PC name comes from composition, and the local-label format names it, so a kept-both label the
        // resolution path produces carries the observed PC name; an empty name (the wrong outcome) fails here.
        var settings = Scenarios.ItemChecks.Settings;
        Check.Require(settings.ComputerName == Environment.MachineName && settings.ComputerName.Length > 0 &&
            settings.ConflictLabelLocalName.Contains("{computerName}", StringComparison.Ordinal),
            "The composition did not supply the PC name for conflict labels");
    }
}
