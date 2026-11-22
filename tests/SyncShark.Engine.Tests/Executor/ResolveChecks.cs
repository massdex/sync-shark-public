using Massdex.SyncShark.Engine.Executor;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Checks notice resolution against the reviewed subject.</summary>
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

        using var store = new TestDoubles.InMemoryStateStore();
        await store.SaveNoticeAsync(pair.PairId, Make(NoticeKind.Info, TextKey.InfoKeptBoth, "item", "id"), default);
        var stored = store.OpenNotices.Single();
        var local = Side0(Side.Local); var remote = Side0(Side.Dropbox);
        await resolve.ApplyAsync(pair, settings, _ => { }, null!, stored with { Path = "other" },
            ResolutionChoice.Dismiss, null, local, remote, store, null!, null!, default);
        Check.Require(store.OpenNotices.Single() == stored, "A stale reviewed token resolved the notice");
        foreach (var choice in new[]{ResolutionChoice.Ignore, ResolutionChoice.Unignore, ResolutionChoice.Retry})
        {
            await resolve.ApplyAsync(pair, settings, _ => { }, null!, stored, choice, null, local, remote, store,
                null!, null!, default);
            Check.Require(store.OpenNotices.Single() == stored, "A dispatcher-owned choice changed the notice twice");
        }
    }

    /// <summary>Only the choices permitted for the reviewed kind may close it.</summary>
    public static async Task DismissAsync()
    {
        var pair = Scenarios.ItemChecks.Pair();
        foreach (var kind in new[]{NoticeKind.Failure, NoticeKind.RootIdentity, NoticeKind.DriveChange,
            NoticeKind.PutBack, NoticeKind.Info, NoticeKind.Skipped})
        {
            using var store = new TestDoubles.InMemoryStateStore();
            var notice = new Notice(0, kind, TextKey.FailChanged, "item", new([], null), DateTimeOffset.UnixEpoch, false);
            await store.SaveNoticeAsync(pair.PairId, notice, default);
            var allowed = kind is NoticeKind.Info or NoticeKind.Skipped;
            try
            {
                await new Resolve().ApplyAsync(pair, Scenarios.ItemChecks.Settings, _ => { }, null!, store.OpenNotices.Single(),
                    ResolutionChoice.Dismiss, null, null!, null!, store, null!, null!, default);
            }
            catch (EngineRequestException e) when (!allowed && e.Key == TextKey.ResolveBadChoice) { }
            Check.Require((store.OpenNotices.Count == 0) == allowed, $"Wrong Dismiss outcome for {kind}");
        }
    }
}
