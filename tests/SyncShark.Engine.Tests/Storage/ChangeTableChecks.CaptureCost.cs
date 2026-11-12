using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;

namespace Massdex.SyncShark.Engine.Tests.Storage;

// B-195b round 7 print-only probe (measure branches only, never merged), byte-identical on 709e794b1 and the candidate:
// the real StateStore.ReadItemRetryForSubject at one path, for an empty path, an identity match and a member-subject
// match (episode recorded under work of another id), with 1, 10 and 100 members at the path, while unrelated synced
// rows vary (1k vs 100k). No threshold: CI prints both sides.
// alloc: bytes the warmed capture call allocates on this thread. sql: statements SQLite ran on the store connection.
// rows: rows ItemEpisodesAtPath returns. miss-rows / miss-json-bytes: of those, members whose identity does not equal
// the capture's identity, and their subject_json bytes: the rows the candidate decodes (the baseline decodes none).
public static partial class ChangeTableChecks
{
    public static void CaptureCosts()
    {
        foreach (var unrelated in new[] { 1_000, 100_000 })
            WithStore((_, database) => CaptureCostsAt(database, unrelated));
    }

    private static void CaptureCostsAt(string database, int unrelated)
    {
        var attempt = Guid.NewGuid();
        using var store = StateStore.Open(database, (id, _, _, _, _) => id == attempt);
        var provenance = Install(store);
        CompletePrerequisite(database);
        var local = store.ReadSource(PairId, Side.Local)!.Value;
        var volume = Pair(PairId).Roots!.Local.VolumeSerialNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var generation = provenance.PairGeneration;
        var scope = new PathScope(store.Pair(PairId)!, store.Pairs());
        for (var start = 0; start < unrelated; start += 10_000)
            store.Commit(PairId, StateChange.Empty with
            {
                Record = Enumerable.Range(start, Math.Min(10_000, unrelated - start)).Select(index =>
                {
                    var path = "Background/f" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    return new SyncedEntry(PathNames.Key(path), path, PathNames.DropboxKey(path), path, false,
                        "background-" + index, 0, 0, "background-remote-" + index, string.Empty);
                }).ToArray()
            });
        var page = 0UL;
        // One held episode at path: its work names workId, its member subject names subjectId; members: rows at the path.
        void Episode(string path, string workId, string subjectId, int members)
        {
            store.AcquireJournalPage(volume, page == 0 ? (JournalPosition?)null : new JournalPosition(0, page),
                new JournalPosition(0, ++page), [(PairId, generation, local.Epoch, local.Revision, false)],
                [(PairId, Notice(Side.Local, path, workId))]);
            var selection = store.SelectWork(PairId, generation, scope) ?? throw new InvalidOperationException(ExpectedFailure);
            Check.Require(store.BeginSelection(selection) is not null, "Selection setup must be admitted.");
            store.RecordInUseFailure(PairId, generation, local.Epoch, path,
                new IssueSubject([new IssueItem(Side.Local, path, subjectId)], null), TextKey.FailLocalInUse, selection,
                ItemRetryDispatch.OrdinaryItem, null, null, attempt);
            store.EndWorkPass(PairId);
            if (members > 1) CloneMembers(database, path, members - 1);
        }
        var cases = new List<(string Case, string Path, string Id, int Members)> { ("empty", "Cost/empty", Identity, 0) };
        foreach (var members in new[] { 1, 10, 100 })
        {
            Episode("Cost/id-" + members, Identity, Identity, members);
            Episode("Cost/subject-" + members, Identity, OtherIdentity, members);
            cases.Add(("identity", "Cost/id-" + members, Identity, members));
            cases.Add(("member-subject", "Cost/subject-" + members, OtherIdentity, members));
        }
        foreach (var (name, path, id, members) in cases)
        {
            var subject = new IssueSubject([new IssueItem(Side.Local, path, id)], null);
            ItemRetryCandidate? found = null;
            var alloc = AllocProbe.Measure(_ => found = store.ReadItemRetryForSubject(PairId, generation, path, subject));
            var sql = TracedStatements(store, () => store.ReadItemRetryForSubject(PairId, generation, path, subject));
            var (rows, missRows, missBytes) = AtPath(database, path, ChangeTable.Identity(Side.Local, id));
            Console.WriteLine($"b195b-capture-cost case={name} members={members} unrelated={unrelated} " +
                $"found={found is not null} alloc={alloc} sql={sql} rows={rows} miss-rows={missRows} " +
                $"miss-json-bytes={missBytes}");
        }
    }

    // Fixture-only: repeats the path's one member row (and its acquired work row) count times, as member history.
    private static void CloneMembers(string database, string path, int count)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = database }.ToString());
        StateStore.OpenConnection(connection);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        const string WorkColumns = "work_origin,issue_work_key,dispatch_kind,pending_operation_id,pending_generation," +
            "pair_id,pair_generation,source_epoch,side,domain,path_key,old_path_key,identity,notice_json,before_json," +
            "acquisition_work_id,parent_anchor,obligation_key";
        command.CommandText =
            "INSERT INTO change_work(" + WorkColumns + ") SELECT " + WorkColumns + " FROM change_work WHERE work_id=$w;" +
            "INSERT INTO issue_member(issue_id,pair_id,work_id,side,path_key,identity,subject_json,skip_cause) " +
            "SELECT issue_id,pair_id,last_insert_rowid(),side,path_key,identity,subject_json,skip_cause FROM issue_member WHERE work_id=$w;";
        using var first = connection.CreateCommand();
        first.Transaction = transaction;
        first.CommandText = "SELECT min(work_id) FROM issue_member WHERE side=0 AND path_key=$k AND work_id IS NOT NULL;";
        first.Parameters.AddWithValue("$k", PathNames.Key(path));
        command.Parameters.AddWithValue("$w", (long)first.ExecuteScalar()!);
        for (var index = 0; index < count; index++) command.ExecuteNonQuery();
        transaction.Commit();
    }

    private static (long Rows, long MissRows, long MissBytes) AtPath(string database, string path, object? identity)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = database, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var pairRow = connection.CreateCommand();
        pairRow.CommandText = "SELECT id FROM pair WHERE pair_id=$p;";
        pairRow.Parameters.AddWithValue("$p", PairId);
        var row = (long)pairRow.ExecuteScalar()!;
        using var members = connection.CreateCommand();
        members.CommandText = StateSchema.ItemEpisodesAtPath;
        members.Parameters.AddWithValue(StateSchema.ParamPairRow, row);
        members.Parameters.AddWithValue(StateSchema.ParamSide, (int)Side.Local);
        members.Parameters.AddWithValue(StateSchema.ParamPath, PathNames.Key(path));
        using var reader = members.ExecuteReader();
        long rows = 0, missRows = 0, missBytes = 0;
        while (reader.Read())
        {
            rows++;
            var memberIdentity = reader.IsDBNull(StateSchema.EpisodePathIdentity) ? null : reader.GetValue(StateSchema.EpisodePathIdentity);
            if (Equals(memberIdentity, identity)) continue;
            missRows++;
            missBytes += System.Text.Encoding.UTF8.GetByteCount(reader.GetString(StateSchema.EpisodePathSubject));
        }
        return (rows, missRows, missBytes);
    }

    private static int TracedStatements(StateStore store, Action action)
    {
        var connection = (Microsoft.Data.Sqlite.SqliteConnection)typeof(StateStore).GetField("_connection",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(store)!;
        var total = 0;
        SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)((_, _) => Interlocked.Increment(ref total)), null);
        try { action(); }
        finally { SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)null!, null); }
        return total;
    }
}
