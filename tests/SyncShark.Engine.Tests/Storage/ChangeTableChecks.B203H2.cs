using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Storage;

public static partial class ChangeTableChecks
{
    // B-203 H2 (rehearsal 0.3.85): the trigger is a RENAME (V1 moved v1.txt -> v1.away while held) or the
    // replacement-id arrival (V3 put v3.old beside a new v3.txt), not a plain same-id return. Both leave a live
    // in-use episode on a LATER row of the same identity while the earlier row becomes unheld. The earlier
    // identity selection's completion DELETE spans [anchor, through] with the anchor's NULL obligation (acquired
    // rows carry none), so it reaches the later episode-named row; issue.item_retry_work_id REFERENCES change_work
    // ON DELETE NO ACTION, so CompleteWork must not delete it and must not throw.
    private static void B203H2Cases(List<string> failures)
    {
        WithStore((_, database) => B203H2Case(database, replacement: false, failures));
        WithStore((_, database) => B203H2Case(database, replacement: true, failures));
    }

    private static void B203H2Case(string database, bool replacement, List<string> failures)
    {
        const string Away = "Parent/File.away";
        var name = replacement ? "replacement-id return" : "renamed away while held";
        var attempt = Guid.NewGuid();
        using var store = StateStore.Open(database, (id, _, _, _, _) => id == attempt);
        var provenance = Install(store);
        CompletePrerequisite(database);
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var volume = Pair(PairId).Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var generation = provenance.PairGeneration;
        var scope = new PathScope(store.Pair(PairId)!, store.Pairs());
        var page = 0UL;
        void Arrive(params ChangeNotice[] notices)
        {
            var through = store.AcquireJournalPage(volume, page == 0 ? null : new JournalPosition(0, page),
                new JournalPosition(0, ++page), [(PairId, generation, local.Epoch, local.Revision, false)],
                notices.Select(notice => (PairId, notice)));
            Check.Require(through is not null, "B-203 H2 setup: the journal page is acquired");
        }
        WorkSelection Begin()
        {
            var selection = store.SelectWork(PairId, generation, scope) ?? throw new InvalidOperationException(ExpectedFailure);
            Check.Require(store.BeginSelection(selection) is not null, "Selection setup must be admitted.");
            return selection;
        }
        IssueSubject Held(string path, string id) => new([new IssueItem(Side.Local, path, id)], null);

        // 1. The file at File is held: its first episode names the earliest identity row.
        Arrive(Notice(Side.Local, File, Identity));
        var first = Begin();
        store.RecordInUseFailure(PairId, generation, local.Epoch, File, Held(File, Identity), TextKey.FailLocalInUse,
            first, ItemRetryDispatch.OrdinaryItem, null, null, attempt);
        store.EndWorkPass(PairId);
        // 2. While still held the file is renamed away (a real Renamed notice with its old path, same id), and the
        //    away path raises its own in-use episode. The earlier row is held, so the rename's selection is the
        //    later row of the same identity. The replacement-id shape also puts the new id at the original path.
        Arrive(replacement
            ? [Notice(Side.Local, Away, Identity, kind: ChangeKind.Renamed, oldPath: File),
               Notice(Side.Local, File, OtherIdentity)]
            : [Notice(Side.Local, Away, Identity, kind: ChangeKind.Renamed, oldPath: File)]);
        var away = Begin();
        Check.Require(away.Identity == Identity && away.AnchorWorkId > first.AnchorWorkId,
            $"B-203 H2 setup ({name}): the renamed item's selection is a later row of the same identity; " +
            $"first={first.AnchorWorkId} away={away.AnchorWorkId} identity={away.Identity}");
        store.RecordInUseFailure(PairId, generation, local.Epoch, Away, Held(Away, Identity), TextKey.FailLocalInUse,
            away, ItemRetryDispatch.OrdinaryItem, null, null, attempt);
        store.EndWorkPass(PairId);
        // 3. Release: the fresh read at File finds no old file (or only the replacement id), so the vanished wait
        //    of the earlier row retires and its notice closes. That row is now unheld.
        foreach (var vanished in store.ReadReplacedItemRetry(PairId, generation, File, replacement ? OtherIdentity : ""))
            store.RetireReplacedItemRetry(vanished, File, replacement ? OtherIdentity : null);
        store.Commit(PairId, StateChange.Empty with { Close = [(File, IssueClosure.AfterEffect)] });
        // 4. The completion of the renamed item's identity range starts at the earlier, now unheld row and reaches
        //    the later row the away episode still names. It must leave that row with its episode.
        var trace = new List<string>();
        for (var turn = 0; turn < 8 && store.SelectWork(PairId, generation, scope) is { } selection; turn++)
        {
            Check.Require(store.BeginSelection(selection) is not null, "Selection setup must be admitted.");
            try
            {
                trace.Add($"sel({selection.Identity},{selection.AnchorWorkId}..{selection.ThroughWorkId})=" +
                    store.CompleteWork(selection, null));
            }
            catch (Microsoft.Data.Sqlite.SqliteException error)
            {
                var detail = $"B-203 H2 ({name}): sel({selection.Identity},{selection.AnchorWorkId}.." +
                    $"{selection.ThroughWorkId}) threw SqliteException: SqliteErrorCode={error.SqliteErrorCode} " +
                    $"SqliteExtendedErrorCode={error.SqliteExtendedErrorCode} Message={error.Message}";
                Console.WriteLine($"b203-h2 REPRODUCED {detail}; {string.Join(' ', trace)}");
                failures.Add(detail);
                return;
            }
            store.EndWorkPass(PairId);
        }
        var awayLive = store.Issues(PairId).Any(issue => issue.Path == Away);
        Console.WriteLine($"b203-h2 NOT-REPRODUCED {name}: {string.Join(' ', trace)} away-notice={awayLive}");
        Check.Require(awayLive, $"B-203 H2 ({name}): the away episode stays live for its own retry");
    }
}
