using Massdex.SyncShark.Engine.Executor;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Contract checks for Notices: one raise/close owner, exact reviewed identity on close, PutBack never settled
/// by the generic close, and the closure-kind sets.</summary>
public static class NoticesChecks
{
    /// <summary>Verifies the Notices contract through the module surface.</summary>
    public static async Task RunAsync()
    {
        var pair = Scenarios.ItemChecks.Pair();
        var notices = new Notices();
        static Notice Make(NoticeKind kind, TextKey cause, string path) =>
            new(0, kind, cause, path, new IssueSubject([new NoticeItem(Side.Local, path, "id")], Side.Local),
                DateTimeOffset.UnixEpoch, false);

        using (var store = new TestDoubles.InMemoryStateStore())
        {
            await notices.RaiseAsync(store, pair.PairId, Make(NoticeKind.Failure, TextKey.FailChanged, "p"), default);
            var stored = store.OpenNotices.Single();
            await notices.CloseAsync(store, pair.PairId, stored with { Path = "other" }, default);
            Check.Require(store.OpenNotices.Count == 1, "A mismatched expected closed the notice");
            await notices.CloseAsync(store, pair.PairId, stored, default);
            Check.Require(store.OpenNotices.Count == 0, "The matching expected did not close the notice");

            // A PutBack notice is not closed by the generic path; its own Retry or a later change settles it.
            await notices.RaiseAsync(store, pair.PairId, Make(NoticeKind.PutBack, TextKey.FailLocalInUse, "p"), default);
            var putBack = store.OpenNotices.Single();
            await notices.CloseAsync(store, pair.PairId, putBack, default);
            Check.Require(store.OpenNotices.Any(n => n.Kind == NoticeKind.PutBack), "Generic close settled a PutBack notice");
        }

        Check.Require(Notices.AfterEffect.SequenceEqual([NoticeKind.Failure]) &&
            Notices.AfterInSync.Contains(NoticeKind.NameClash) &&
            Notices.AfterHeldInSync.Contains(NoticeKind.Tie) && Notices.AfterHeldInSync.Contains(NoticeKind.NameClash) &&
            Notices.AfterKeepBoth.Contains(NoticeKind.Tie) && !Notices.AfterKeepBoth.Contains(NoticeKind.NameClash),
            "Closure kind sets drifted");
    }
}
