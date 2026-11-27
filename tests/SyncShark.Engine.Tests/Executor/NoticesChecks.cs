using Massdex.SyncShark.Engine.Executor;

namespace Massdex.SyncShark.Engine.Tests.Executor;

/// <summary>Contract checks for Notices: one raise/close owner, exact reviewed identity on close, and PutBack never settled
/// by the generic close.</summary>
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
            Check.Require(!await notices.CloseAsync(store, pair.PairId, stored with { Path = "other" }, default),
                "Conditional close reported success for a stale subject");
            Check.Require(store.OpenNotices.Count == 1, "A mismatched expected closed the notice");
            await store.SaveNoticeAsync(pair.PairId, stored with { Subject = new([new(Side.Local, "p", "replacement")], Side.Local) }, default);
            Check.Require(!await notices.CloseAsync(store, pair.PairId, stored, default), "Close consumed a replacement in the reviewed slot");
            stored = store.OpenNotices.Single();
            Check.Require(stored.Subject.Items.Single().Id == "replacement" && await notices.CloseAsync(store, pair.PairId, stored, default),
                "The matching successor was not closable");
            Check.Require(store.OpenNotices.Count == 0, "The matching expected did not close the notice");

            // A PutBack notice is not closed by the generic path; its own Retry or a later change settles it.
            await notices.RaiseAsync(store, pair.PairId, Make(NoticeKind.PutBack, TextKey.FailLocalInUse, "p"), default);
            var putBack = store.OpenNotices.Single();
            var closed = await notices.CloseAsync(store, pair.PairId, putBack, default);
            Check.Require(!closed && store.OpenNotices.Single() == putBack, "Generic close settled a PutBack notice");
        }
    }
}
