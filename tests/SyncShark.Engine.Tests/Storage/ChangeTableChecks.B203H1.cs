using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Storage;

public static partial class ChangeTableChecks
{
    // B-203 (H1): the completion range holds a SECOND Acquired row of the same identity that a live item episode
    // names, and the anchor is an earlier row. Live shape (rehearsal 0.3.85, V2): the held file raised its wait, was
    // moved to a sibling path while still held (a second episode there, on the later row), then the vanished first
    // path's wait retired and its notice closed. The earlier identity selection completes with a DELETE over
    // [anchor, through] that reaches the later row: issue.item_retry_work_id REFERENCES change_work ON DELETE NO ACTION.
    private static void EarlierIdentitySelectionSparesLaterEpisode(string database)
    {
        var attempt = Guid.NewGuid();
        using var store = StateStore.Open(database, (id, _, _, _, _) => id == attempt);
        var provenance = Install(store);
        CompletePrerequisite(database);
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var volume = Pair(PairId).Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var generation = provenance.PairGeneration;
        var scope = new PathScope(store.Pair(PairId)!, store.Pairs());
        const string Away = "Parent/File.away";
        var page = 0UL;
        void Arrive(ChangeNotice notice)
        {
            var through = store.AcquireJournalPage(volume, page == 0 ? null : new JournalPosition(0, page),
                new JournalPosition(0, ++page), [(PairId, generation, local.Epoch, local.Revision, false)],
                [(PairId, notice)]);
            Check.Require(through is not null, "B-203 H1 setup: the journal page is acquired");
        }
        WorkSelection Begin()
        {
            var selection = store.SelectWork(PairId, generation, scope) ??
                throw new InvalidOperationException(ExpectedFailure);
            Check.Require(store.BeginSelection(selection) is not null, "Selection setup must be admitted.");
            return selection;
        }
        IssueSubject Held(string path) => new([new IssueItem(Side.Local, path, Identity)], null);

        // 1. The held file's own wait: its first episode, on the earliest Acquired row of its identity.
        Arrive(Notice(Side.Local, File, Identity));
        store.RecordInUseFailure(PairId, generation, local.Epoch, File, Held(File), TextKey.FailLocalInUse,
            Begin(), ItemRetryDispatch.OrdinaryItem, null, null, attempt);
        store.EndWorkPass(PairId);
        // 2. Moved away while still held: a later row of the same identity at another path. The earlier row is held, so
        // the selection is that later row and the .away episode names it.
        Arrive(Notice(Side.Local, Away, Identity));
        store.RecordInUseFailure(PairId, generation, local.Epoch, Away, Held(Away), TextKey.FailLocalInUse,
            Begin(), ItemRetryDispatch.OrdinaryItem, null, null, attempt);
        store.EndWorkPass(PairId);
        // 3. The .txt wait retires (its fresh read finds no file) and its notice closes; its row is no longer held.
        foreach (var vanished in store.ReadReplacedItemRetry(PairId, generation, File, ""))
            store.RetireReplacedItemRetry(vanished, File, null);
        store.Commit(PairId, StateChange.Empty with { Close = [(File, IssueClosure.AfterEffect)] });
        // 4. The earlier identity selection is the earliest remaining row; the .away episode still names a later row in
        // that selection's range. Completing it must not delete that row.
        var completing = Begin();
        var away = store.Issues(PairId).SingleOrDefault(issue => issue.Path == Away) ??
            throw new InvalidOperationException("B-203 H1 setup: the .away notice is live");
        var episode = store.ReadItemRetryState(PairId, [away.Id])[away.Id] ??
            throw new InvalidOperationException("B-203 H1 setup: the .away episode is live on its later row");
        Check.Require(episode.Token.WorkId > completing.AnchorWorkId &&
            episode.Token.WorkId <= completing.ThroughWorkId,
            "B-203 H1 setup: the .away episode names a later row inside the completing selection's range");
        try
        {
            var retired = store.CompleteWork(completing, null);
            Console.WriteLine($"b203-h1: completed selection {completing.AnchorWorkId}..{completing.ThroughWorkId} " +
                $"retired={retired} episode-work={episode.Token.WorkId} notices={store.Issues(PairId).Count}");
        }
        catch (Microsoft.Data.Sqlite.SqliteException error)
        {
            Console.WriteLine($"b203-h1: completing selection {completing.AnchorWorkId}..{completing.ThroughWorkId} " +
                $"with live episode work {episode.Token.WorkId} threw SqliteException: " +
                $"SqliteErrorCode={error.SqliteErrorCode} SqliteExtendedErrorCode={error.SqliteExtendedErrorCode} " +
                $"Message={error.Message}");
            throw;
        }
    }
}
