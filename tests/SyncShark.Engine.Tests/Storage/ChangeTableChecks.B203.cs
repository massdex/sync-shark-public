using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Storage;

public static partial class ChangeTableChecks
{
    // B-203 (rehearsal V2 as logged, Astra diagnosis): the held file at the path has its episode; it moves to another
    // path while held and a SEPARATE episode is recorded there on its own later work of the same identity. The path's
    // vanished wait retires and its notice closes, so the earlier work is unheld and memberless; the file returns.
    // Completing the earlier selection must leave the other path's episode work alone and must not throw.
    private static void EpisodeElsewhereCompletes(string database, string returned, bool memberOnly = false)
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
        IssueSubject At(string path, string id) => new([new IssueItem(Side.Local, path, id)], null);
        Arrive(Notice(Side.Local, File, Identity));
        store.RecordInUseFailure(PairId, generation, local.Epoch, File, At(File, Identity), TextKey.FailLocalInUse, Begin(),
            ItemRetryDispatch.OrdinaryItem, null, null, attempt);
        store.EndWorkPass(PairId);
        Arrive(Notice(Side.Local, Away, Identity));
        var away = Begin();
        store.RecordInUseFailure(PairId, generation, local.Epoch, Away, At(Away, Identity), TextKey.FailLocalInUse, away,
            ItemRetryDispatch.OrdinaryItem, null, null, attempt);
        store.EndWorkPass(PairId);
        if (memberOnly) Sql(database, "UPDATE issue SET item_retry_episode=NULL,item_retry_revision=NULL,item_retry_work_id=NULL WHERE path=$path;", Away);
        foreach (var vanished in store.ReadReplacedItemRetry(PairId, generation, File, ""))
            store.RetireReplacedItemRetry(vanished, File, null);
        store.Commit(PairId, StateChange.Empty with { Close = [(File, IssueClosure.AfterEffect)] });
        Arrive(Notice(Side.Local, File, returned));
        // This is the persisted state the live host crashed on (the failing completion rolled back, so a crash leaves
        // exactly these rows). Restart: reopen the store on the same file and run the pass to its end.
        store.Dispose();
        using var restarted = StateStore.Open(database, (id, _, _, _, _) => id == attempt);
        var trace = new List<string> { $"away-episode-work={away.AnchorWorkId}" };
        var turns = 0;
        for (; turns < 8 && restarted.SelectWork(PairId, generation, scope) is { } selection; turns++)
        {
            Check.Require(restarted.BeginSelection(selection) is not null, "Selection setup must be admitted.");
            try
            {
                trace.Add($"sel({selection.Identity},{selection.AnchorWorkId}..{selection.ThroughWorkId})=" +
                    restarted.CompleteWork(selection, null));
            }
            catch (Microsoft.Data.Sqlite.SqliteException error)
            {
                throw new InvalidOperationException($"B-203 (episode elsewhere, returned {returned}): completing the " +
                    $"earlier selection must leave the other path's episode work: {error.Message}; " +
                    $"{string.Join(' ', trace)} sel({selection.Identity},{selection.AnchorWorkId}..{selection.ThroughWorkId})", error);
            }
            restarted.EndWorkPass(PairId);
        }
        var (pending, _) = restarted.CountWork(PairId, generation, long.MaxValue);
        trace.Add($"turns={turns} pending-after-restart={pending}");
        Check.Require(memberOnly || turns < 8, $"B-203 (episode elsewhere, returned {returned}): the restarted pass runs to its end: {string.Join(' ', trace)}");
        var awayIssue = restarted.Issues(PairId).Single(issue => issue.Path == Away);
        // Fixture-only member-ownership shape: the Away issue keeps its member on the later row but names no episode;
        // completing the earlier range must not CASCADE-delete that member.
        if (memberOnly)
        {
            Check.Require(Sql(database, "SELECT COUNT(*) FROM issue_member WHERE path_key=$path AND work_id IS NOT NULL;",
                PathNames.Key(Away)) == 1, $"B-203 (member elsewhere, returned {returned}): the other path's issue member stays: {string.Join(' ', trace)}");
            return;
        }
        Check.Require(restarted.ReadItemRetryState(PairId, [awayIssue.Id])[awayIssue.Id] is not null,
            $"B-203 (episode elsewhere, returned {returned}): the other path's episode stays live: {string.Join(' ', trace)}");
        Console.WriteLine($"b203-elsewhere returned={returned} {string.Join(' ', trace)}");
    }

    // B-203 candidate 2 (rehearsal C28, DeepSeek timeline): a case-only rename while held. The episode stays recorded
    // under the old spelling while the file is now listed under the new one (and Dropbox has neither); later work of
    // the same identity arrives under the new spelling. Completing every selection must not throw.
    private static void CaseRenameHeldCompletes(string database)
    {
        var attempt = Guid.NewGuid();
        using var store = StateStore.Open(database, (id, _, _, _, _) => id == attempt);
        var provenance = Install(store);
        CompletePrerequisite(database);
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var volume = Pair(PairId).Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var generation = provenance.PairGeneration;
        var scope = new PathScope(store.Pair(PairId)!, store.Pairs());
        const string Lower = "Parent/lower.txt";
        const string Upper = "Parent/LOWER.txt";
        var page = 0UL;
        void Arrive(params ChangeNotice[] notices) => store.AcquireJournalPage(volume,
            page == 0 ? (JournalPosition?)null : new JournalPosition(0, page), new JournalPosition(0, ++page),
            [(PairId, generation, local.Epoch, local.Revision, false)], notices.Select(notice => (PairId, notice)));
        Arrive(Notice(Side.Local, Lower, Identity));
        var held = store.SelectWork(PairId, generation, scope) ?? throw new InvalidOperationException(ExpectedFailure);
        Check.Require(store.BeginSelection(held) is not null, "Selection setup must be admitted.");
        store.RecordInUseFailure(PairId, generation, local.Epoch, Lower, new IssueSubject([new IssueItem(Side.Local, Lower,
            Identity)], null), TextKey.FailLocalInUse, held, ItemRetryDispatch.OrdinaryItem, null, null, attempt);
        store.EndWorkPass(PairId);
        Arrive(Notice(Side.Local, Upper, Identity, oldPath: Lower));
        Arrive(Notice(Side.Local, Upper, Identity));
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
                throw new InvalidOperationException($"B-203 (case rename held): completing must not throw: {error.Message}; " +
                    $"{string.Join(' ', trace)} sel({selection.Identity},{selection.AnchorWorkId}..{selection.ThroughWorkId})", error);
            }
            store.EndWorkPass(PairId);
        }
        Console.WriteLine($"b203-case held-work={held.AnchorWorkId} {string.Join(' ', trace)}");
    }

    private static long Sql(string database, string text, string path)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = database, Pooling = false }.ToString());
        connection.Open();
        // The issue CHECK names the store's registered function; this fixture write only clears the episode fields.
        connection.CreateFunction(StateSchema.LocalAccessRetryCauseFunction, (string? _) => 1);
        using var command = connection.CreateCommand();
        command.CommandText = text;
        command.Parameters.AddWithValue("$path", path);
        return text.StartsWith("SELECT", StringComparison.Ordinal) ? (long)command.ExecuteScalar()! : command.ExecuteNonQuery();
    }
}
