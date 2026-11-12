using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Storage;

public static partial class ChangeTableChecks
{
    // B-203 (rehearsal V2/V3 on the real store): a held file moves away while still held, its vanished wait retires,
    // then a file returns at the path (the same id, or a replacement id) while still held and is held again, then is
    // released. Completing every selection that follows must never throw (the live host died on a SqliteException in
    // ChangeTable.Retire at the pass end): work a live episode still names stays with that episode.
    private static void VanishedReturnCompletes(string database, string returned, bool awayHeld, bool closePath = true, bool retireAway = true)
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
        void Arrive(params ChangeNotice[] notices) => store.AcquireJournalPage(volume,
            page == 0 ? (JournalPosition?)null : new JournalPosition(0, page), new JournalPosition(0, ++page),
            [(PairId, generation, local.Epoch, local.Revision, false)], notices.Select(notice => (PairId, notice)));
        WorkSelection Begin()
        {
            var selection = store.SelectWork(PairId, generation, scope) ?? throw new InvalidOperationException(ExpectedFailure);
            Check.Require(store.BeginSelection(selection) is not null, "Selection setup must be admitted.");
            return selection;
        }
        IssueSubject Held(string id) => new([new IssueItem(Side.Local, File, id)], null);
        // V2/V3 step 1: the saved file is held.
        Arrive(Notice(Side.Local, File, Identity));
        store.RecordInUseFailure(PairId, generation, local.Epoch, File, Held(Identity), TextKey.FailLocalInUse, Begin(),
            ItemRetryDispatch.OrdinaryItem, null, null, attempt);
        store.EndWorkPass(PairId);
        // Step 2: moved away while held (same id at another path, held there too: its own in-use notice), then the
        // fresh read at the path finds no file: the path's wait retires.
        Arrive(Notice(Side.Local, File, Identity, kind: ChangeKind.Deleted), Notice(Side.Local, Away, Identity));
        if (awayHeld)
        {
            try
            {
                store.RecordInUseFailure(PairId, generation, local.Epoch, Away, new IssueSubject([new IssueItem(Side.Local,
                    Away, Identity)], null), TextKey.FailLocalInUse, Begin(), ItemRetryDispatch.OrdinaryItem, null, null, attempt);
            }
            catch (LiveAdmissionStaleException error) { Console.WriteLine("b203-repro away-held setup refused: " + error.Reason); }
            store.EndWorkPass(PairId);
        }
        foreach (var vanished in store.ReadReplacedItemRetry(PairId, generation, File, ""))
            store.RetireReplacedItemRetry(vanished, File, null);
        // The path's notice then closes with the path's own work (its members go; its older work stays pending).
        if (closePath) store.Commit(PairId, StateChange.Empty with { Close = [(File, IssueClosure.AfterEffect)] });
        // Step 3: a file returns at the path while still held (moved back, or a replacement) and is held again.
        Arrive(Notice(Side.Local, Away, Identity, kind: ChangeKind.Deleted), Notice(Side.Local, File, returned));
        if (awayHeld && retireAway)
            foreach (var vanished in store.ReadReplacedItemRetry(PairId, generation, Away, ""))
                store.RetireReplacedItemRetry(vanished, Away, null);
        store.RecordInUseFailure(PairId, generation, local.Epoch, File, Held(returned), TextKey.FailLocalInUse, Begin(),
            ItemRetryDispatch.OrdinaryItem, null, null, attempt);
        store.EndWorkPass(PairId);
        // Step 4: released; the upload's selections complete as an ordinary pass does.
        Arrive(Notice(Side.Local, File, returned));
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
                throw new InvalidOperationException($"B-203 (returned {returned}, away held {awayHeld}, close {closePath}, retire away {retireAway}): completing a selection after the " +
                    $"vanished wait retired and the file returned held must not throw: {error.Message}; " +
                    $"{string.Join(' ', trace)} sel({selection.Identity},{selection.AnchorWorkId}..{selection.ThroughWorkId})", error);
            }
            store.EndWorkPass(PairId);
        }
        Console.WriteLine($"b203-repro returned={returned} away-held={awayHeld} close={closePath} retire-away={retireAway} {string.Join(' ', trace)} notices={store.Issues(PairId).Count}");
    }
}
