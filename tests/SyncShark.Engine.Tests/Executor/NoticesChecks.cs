using Massdex.SyncShark.Engine.Executor;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Contract checks for Notices: one raise/close owner, exact reviewed identity on close, and the F147 rule
/// that only a PutBack notice's own Retry or a later change of the same item settles it.</summary>
public static class NoticesChecks
{
    /// <summary>Verifies the Notices contract through the module surface.</summary>
    public static async Task RunAsync()
    {
        const string pair = "pair";
        using var store = new TestDoubles.InMemoryStateStore();
        var notices = new Notices();
        var ordinary = new Notice(1, NoticeKind.Failure, TextKey.FailChanged, "path",
            new IssueSubject([new NoticeItem(Side.Local, "path", "id")], Side.Local), DateTimeOffset.UnixEpoch, false);

        await notices.RaiseAsync(store, pair, ordinary, default);
        Check.Require(store.OpenNotices.Count == 1 && store.OpenNotices.Single().Matches(ordinary), "Raised notice absent");
        // A stale reviewed identity (another path) must not close the stored notice.
        await notices.CloseAsync(store, pair, ordinary with { Path = "other" }, default);
        Check.Require(store.OpenNotices.Count == 1, "A mismatched expected closed the notice");
        await notices.CloseAsync(store, pair, ordinary, default);
        Check.Require(store.OpenNotices.Count == 0, "The matching expected did not close the notice");

        // F147: a PutBack notice survives Ignore and is settled only by its own Retry or a later change of the item.
        var putBack = ordinary with
        {
            Id = 2, Kind = NoticeKind.PutBack, Cause = TextKey.FailLocalInUse,
            Subject = new IssueSubject([new NoticeItem(Side.Local, "path", "id")], Side.Local)
        };
        await notices.RaiseAsync(store, pair, putBack, default);
        Check.Require(store.OpenNotices.Any(n => n.Matches(putBack)) && !Notices.PutBackSettled(false),
            "A compare outcome settled or dropped a PutBack notice");
        await notices.RaiseAsync(store, pair, putBack with { Ignored = true }, default);
        Check.Require(store.OpenNotices.Single(n => n.Kind == NoticeKind.PutBack).Ignored,
            "Ignore did not preserve the PutBack notice and its flag");
        await notices.CloseAsync(store, pair, putBack with { Ignored = true }, default);
        Check.Require(store.OpenNotices.All(n => n.Kind != NoticeKind.PutBack) && Notices.PutBackSettled(true),
            "The PutBack notice's own Retry did not settle it");

        // The closure kind sets its completed call sites use.
        Check.Require(Notices.AfterEffect.SequenceEqual([NoticeKind.Failure]) &&
            Notices.AfterInSync.Contains(NoticeKind.NameClash) &&
            Notices.AfterHeldInSync.Contains(NoticeKind.Tie) && Notices.AfterHeldInSync.Contains(NoticeKind.NameClash) &&
            Notices.AfterKeepBoth.Contains(NoticeKind.Tie) && !Notices.AfterKeepBoth.Contains(NoticeKind.NameClash),
            "Closure kind sets drifted");
    }
}
