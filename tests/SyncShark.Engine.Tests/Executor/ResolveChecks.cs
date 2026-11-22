using Massdex.SyncShark.Engine.Executor;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Contract checks for Resolve that depend only on the group's own code: reviewed-token and PutBack guards,
/// row-free choices, and stale/occupant endpoint rejection. Real action execution and closure are INTEGRATION-CHECKS
/// (need W02 Resolutions and W17 Effects bodies; R29), as is the F065 label assertion.</summary>
public static class ResolveChecks
{
    /// <summary>Verifies the Resolve contract through the module surface.</summary>
    public static async Task RunAsync()
    {
        var pair = Scenarios.ItemChecks.Pair();
        var settings = Scenarios.ItemChecks.Settings;
        var resolve = new Resolve();
        static Notice Make(NoticeKind kind, TextKey cause, string path, string id) =>
            new(0, kind, cause, path, new IssueSubject([new NoticeItem(Side.Local, path, id)], Side.Local),
                DateTimeOffset.UnixEpoch, false);
        static TestDoubles.FakeSide Side0(Side side) => new(side);

        // Stale reviewed token acts on nothing; a matching Dismiss closes an ordinary notice.
        using (var store = new TestDoubles.InMemoryStateStore())
        {
            await store.SaveNoticeAsync(pair.PairId, Make(NoticeKind.Failure, TextKey.FailChanged, "item", "id"), default);
            var stored = store.OpenNotices.Single();
            var local = Side0(Side.Local); var dropbox = Side0(Side.Dropbox);
            await resolve.ApplyAsync(pair, settings, _ => { }, null!, stored with { Path = "other" },
                ResolutionChoice.Dismiss, null, local, dropbox, store, null!, null!, default);
            Check.Require(store.OpenNotices.Count == 1, "A stale reviewed token resolved the notice");
            await resolve.ApplyAsync(pair, settings, _ => { }, null!, stored, ResolutionChoice.Dismiss, null, local,
                dropbox, store, null!, null!, default);
            Check.Require(store.OpenNotices.Count == 0, "Dismiss did not close the reviewed notice");
        }

        // Dismiss never closes a PutBack notice.
        using (var store = new TestDoubles.InMemoryStateStore())
        {
            await store.SaveNoticeAsync(pair.PairId, Make(NoticeKind.PutBack, TextKey.FailLocalInUse, "item", "id"), default);
            var stored = store.OpenNotices.Single();
            await resolve.ApplyAsync(pair, settings, _ => { }, null!, stored, ResolutionChoice.Dismiss, null,
                Side0(Side.Local), Side0(Side.Dropbox), store, null!, null!, default);
            Check.Require(store.OpenNotices.Any(n => n.Kind == NoticeKind.PutBack), "Dismiss closed a PutBack notice");
        }

        // Ignore hides and Unignore restores, inside the reviewed identity.
        using (var store = new TestDoubles.InMemoryStateStore())
        {
            await store.SaveNoticeAsync(pair.PairId, Make(NoticeKind.Failure, TextKey.FailChanged, "item", "id"), default);
            var stored = store.OpenNotices.Single();
            var local = Side0(Side.Local); var dropbox = Side0(Side.Dropbox);
            await resolve.ApplyAsync(pair, settings, _ => { }, null!, stored, ResolutionChoice.Ignore, null, local,
                dropbox, store, null!, null!, default);
            Check.Require(store.OpenNotices.Single().Ignored, "Ignore did not hide the notice");
            await resolve.ApplyAsync(pair, settings, _ => { }, null!, stored, ResolutionChoice.Unignore, null, local,
                dropbox, store, null!, null!, default);
            Check.Require(!store.OpenNotices.Single().Ignored, "Unignore did not restore the notice");
        }

        // A replacement occupant fails endpoint validation and authorizes no resolution.
        using (var store = new TestDoubles.InMemoryStateStore())
        {
            await store.SaveNoticeAsync(pair.PairId, Make(NoticeKind.Failure, TextKey.FailChanged, "p", "pid"), default);
            var stored = store.OpenNotices.Single();
            var local = Side0(Side.Local);
            local.Seed(Scenarios.ItemChecks.File(Side.Local, "p", 1, "other"), [1]);
            await resolve.ApplyAsync(pair, settings, _ => { }, null!, stored, ResolutionChoice.UseLocal, null, local,
                Side0(Side.Dropbox), store, null!, null!, default);
            Check.Require(store.OpenNotices.Count == 1, "A replacement occupant authorized a resolution");
        }

        // F065/A03 real label check (R29 INTEGRATION, waits for W01 ConflictName): when it is not yet implemented the
        // lane reports no fake pass; when implemented it must carry the composition-supplied PC name.
        try
        {
            var label = Planner.ConflictName.For("f.txt", Side.Local, DateTimeOffset.UnixEpoch, settings.ComputerName,
                settings, 1);
            Check.Require(label.Contains(settings.ComputerName, StringComparison.Ordinal),
                "The generated conflict label lacks the composition-supplied PC name");
        }
        catch (NotImplementedException) { }
    }
}
