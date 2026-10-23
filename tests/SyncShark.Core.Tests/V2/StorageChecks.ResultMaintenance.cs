namespace Massdex.SyncShark.V2;

internal static partial class StorageChecks
{
    private static void ResultMaintenanceChecks(string folder)
    {
        try
        {
            using (var store = SqliteStateStore.Open(folder, Fast, TimeProvider.System))
            {
                store.ResultMaintenanceForTest = () => throw new IOException("injected post-commit cleanup failure");
                CommitDoneBaselineAndIntent(store);
                var notice = store.OpenNotices("p1").Single(n => n.ReasonKey == "store-maintenance");
                Require(notice.Detail.Contains("injected post-commit cleanup failure") &&
                    NoticeChoices.Options(notice).Any(o => o.Choice == NoticeChoices.RetryNow) &&
                    NoticeChoices.Options(notice).Any(o => o.Choice == NoticeChoices.Ignore),
                    "post-commit failure is actionable maintenance, not failed file result");
                store.Prune();
                Require(store.OpenNotices("p1").Count(n => n.ReasonKey == "store-maintenance") == 1,
                    "repeated maintenance failure retains one notice");
            }
            using (var reopened = SqliteStateStore.Open(folder, Fast, TimeProvider.System))
            {
                Require(reopened.UnresolvedIntents("p1").Count == 0 && reopened.Baselines("p1").Count == 1,
                    "cold reopen retains committed baseline and settled intent after cleanup fault");
                reopened.Prune();
                Require(!reopened.OpenNotices("p1").Any(n => n.ReasonKey == "store-maintenance"),
                    "next maintenance pass resolves notice without replaying file effect");
            }
        }
        finally { LocalJournalChecks.RemoveFixture(folder); }
    }
}
