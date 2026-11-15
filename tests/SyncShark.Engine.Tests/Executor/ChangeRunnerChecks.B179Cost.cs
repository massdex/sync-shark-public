using System.Reflection;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using StateSchema = Massdex.SyncShark.Engine.Storage.StateSchema;

namespace Massdex.SyncShark.Engine.Tests.Executor;

// B-179b cost probe, print-only (no threshold): the same file on fix/0383-b179c and on measure/0383-b179c-base
// (0.3.82), so CI prints before and after for one selection pass per case. Fake providers and real Storage; the
// numbers are counted work, not native timings.
public static partial class ChangeRunnerChecks
{
    public static void RunB179Costs() => B179CostsAsync().GetAwaiter().GetResult();

    private static async Task B179CostsAsync()
    {
        foreach (var name in new[] { "stable", "stale", "late-race", "baseline-bypass", "held" })
        for (var run = 0; run < 2; run++)
            await B179CostAsync(name, run);
        B179GateCost();
    }

    // One case: provider ReadOne calls, admissions, store calls (all, RefuseLaterEvidence, HeldCovers), SQL
    // statements traced on the store's own connection, and bytes allocated by the whole pass.
    private static async Task B179CostAsync(string name, int run)
    {
        using var fixture = new Fixture(realStore: true);
        WorkSelection selection;
        if (name == "held")
        {
            var folder = FolderEntry(Folder, LocalId + "-folder");
            var child = LocalEntry(Folder + "/child", LocalId);
            fixture.Local.Seed(folder, null);
            fixture.Local.Seed(child, [1]);
            fixture.Inner.Commit(PairId, StateChange.Empty with
            {
                Raise = [(IssueKind.NameClash, TextKey.ClashNameTaken, Folder,
                    new IssueSubject([new IssueItem(Side.Local, Folder, folder.Id)], null))]
            });
            selection = Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, child.Path, null, child.Id,
                child, false, false));
            Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, child.Path, null, child.Id, child, false,
                false), page: 2);
        }
        else if (name == "baseline-bypass")
        {
            ClearObligations(fixture);
            var saved = LocalEntry(Item, LocalId);
            var remote = RemoteEntry(Item, DropboxId);
            fixture.Dropbox.Seed(remote, [1]);
            fixture.Local.Seed(saved, [1]);
            fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(saved, remote)] });
            selection = Selected(fixture, Item, saved);
            // Later same-path evidence that leaves the file at its baseline: the early check is bypassed.
            Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null, LocalId, saved, false, false),
                page: 2);
        }
        else
        {
            selection = SavedRewrite(fixture);
            if (name == "stale") RewriteAgain(fixture);
        }
        var rewritten = false;
        var dropbox = new CountingSide(name != "late-race" ? fixture.Dropbox : new HookedSide(fixture.Dropbox,
            beforeRead: path =>
            {
                if (rewritten || !PathNames.Same(path, Item)) return;
                rewritten = true;
                RewriteAgain(fixture);
            }));
        fixture.DropboxView = dropbox;
        var boundary = Validating(fixture);
        var pass = ValidatedRunner(fixture, selection, boundary);
        var calls = new Dictionary<string, int>(StringComparer.Ordinal);
        fixture.Hooks.BeforeCall = method => calls[method.Name] = calls.GetValueOrDefault(method.Name) + 1;
        var connection = (Microsoft.Data.Sqlite.SqliteConnection)fixture.Inner.GetType().GetField("_connection",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Inner)!;
        var statements = 0;
        SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)((_, _) => statements++), null);
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        string outcome;
        try { outcome = (await pass.RunAsync(selection, default)).Completed?.ToString() ?? "null"; }
        catch (LiveAdmissionStaleException error) { outcome = "stale-" + error.Reason; }
        finally
        {
            allocated = GC.GetTotalAllocatedBytes(precise: true) - allocated;
            SQLitePCL.raw.sqlite3_trace(connection.Handle, (SQLitePCL.delegate_trace)null!, null);
            fixture.Hooks.BeforeCall = null;
        }
        Console.WriteLine($"b179c-cost case={name} run={run} outcome={outcome} dropbox-reads={dropbox.Reads} " +
            $"admissions={boundary.Calls} store-calls={calls.Values.Sum()} " +
            $"refuse-later-evidence={calls.GetValueOrDefault(nameof(IStateStore.RefuseLaterEvidence))} " +
            $"held-covers={calls.GetValueOrDefault(nameof(IStateStore.HeldCovers))} sql-statements={statements} " +
            $"allocated-bytes={allocated} " + EvidenceCost(fixture, selection, name == "held" ? Folder + "/child" : Item));
    }

    // Per-call row and VM work of the two exact later-evidence probes RefuseLaterEvidence runs for one path, at the
    // state the pass left: the SQL watermark is BeforeFirstWork, so every retained same-path row is read and the
    // acquisition filter runs in C#. Rows are counted on a read-only connection; VM steps come from CostProbe.
    private static string EvidenceCost(Fixture fixture, WorkSelection selection, string path)
    {
        var database = fixture.DatabasePath!;
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
            { DataSource = database, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var pairRow = connection.CreateCommand();
        pairRow.CommandText = "SELECT id FROM pair WHERE pair_id=$p;";
        pairRow.Parameters.AddWithValue("$p", PairId);
        var row = (long)pairRow.ExecuteScalar()!;
        (string Name, object? Value)[] arguments =
        [
            (StateSchema.ParamPairRow, row), (StateSchema.ParamGeneration, selection.PairGeneration),
            (StateSchema.ParamSide, (int)Side.Local), (StateSchema.ParamEpoch, selection.SourceEpoch),
            (StateSchema.ParamDomain, selection.Domain), (StateSchema.ParamWatermark, 0L),
            (StateSchema.ParamJoinKey, PathNames.KeyFor(Side.Local, path))
        ];
        int Rows(string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in arguments) command.Parameters.AddWithValue(name, value);
            using var reader = command.ExecuteReader();
            var rows = 0;
            while (reader.Read()) rows++;
            return rows;
        }
        using var probe = new CostProbe(database);
        var vm = new[] { StateSchema.EvidencePathExact, StateSchema.EvidenceOldPathExact }
            .Select(sql => probe.Execute(sql, arguments).VmSteps).ToArray();
        return $"evidence-rows={Rows(StateSchema.EvidencePathExact)}+{Rows(StateSchema.EvidenceOldPathExact)} " +
            $"evidence-vm={vm[0]}+{vm[1]}";
    }

    // The gate cost of the one call the early check adds (a non-refusing RefuseLaterEvidence), measured alone and
    // then beside a concurrent store user while a file Record is queued before each call. Wall-clock Stopwatch ticks
    // on the CI machine, print-only: alone approximates the hold, contended minus alone the wait. "drained" counts
    // the queued Records already committed when the call returned (by this call's Write drain or the flusher).
    private static void B179GateCost()
    {
        const int Calls = 200;
        using var fixture = new Fixture(realStore: true);
        var selection = SavedRewrite(fixture);
        var scope = LockKeys.ForLivePaths(fixture.Pair, [(Side.Local, Item)], false);
        long Time(Action call)
        {
            var start = System.Diagnostics.Stopwatch.GetTimestamp();
            call();
            return System.Diagnostics.Stopwatch.GetTimestamp() - start;
        }
        void Refuse() => fixture.Inner.RefuseLaterEvidence(selection, scope);
        Refuse();
        var alone = Enumerable.Range(0, Calls).Select(_ => Time(Refuse)).Order().ToArray();
        using var stop = new CancellationTokenSource();
        var concurrent = Task.Run(() =>
        {
            var count = 0;
            while (!stop.IsCancellationRequested)
            {
                fixture.Inner.ReadWorkWatermark();
                count++;
            }
            return count;
        });
        var records = new List<Task>();
        var drained = 0;
        var contended = new long[Calls];
        for (var index = 0; index < Calls; index++)
        {
            var queued = LocalEntry("queued-" + index, "queued-id-" + index);
            var record = fixture.Inner.CommitRecordAsync(PairId, StateChange.Empty with
            {
                Record = [SyncedEntries.From(queued, RemoteEntry(queued.Path, "queued-remote-" + index))]
            }, null);
            records.Add(record);
            contended[index] = Time(Refuse);
            if (record.IsCompleted) drained++;
        }
        stop.Cancel();
        var concurrentCalls = concurrent.GetAwaiter().GetResult();
        Task.WaitAll(records.ToArray());
        Array.Sort(contended);
        static double Micro(long ticks) => ticks * 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency;
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        Console.WriteLine("b179c-gate calls=" + Calls + " alone-median-us=" + Micro(alone[Calls / 2]).ToString("F1", invariant) +
            " alone-max-us=" + Micro(alone[^1]).ToString("F1", invariant) +
            " contended-median-us=" + Micro(contended[Calls / 2]).ToString("F1", invariant) +
            " contended-max-us=" + Micro(contended[^1]).ToString("F1", invariant) +
            " queued-records-drained-by-return=" + drained + " concurrent-store-calls=" + concurrentCalls);
    }

    // A saved file rewritten (byte 2) and selected; RewriteAgain is the newest rewrite (byte 3) after the selection.
    private static WorkSelection SavedRewrite(Fixture fixture)
    {
        ClearObligations(fixture);
        var saved = LocalEntry(Item, LocalId);
        var remote = RemoteEntry(Item, DropboxId);
        fixture.Dropbox.Seed(remote, [1]);
        fixture.Inner.Commit(PairId, StateChange.Empty with { Record = [SyncedEntries.From(saved, remote)] });
        var changed = saved with { Modified = Stamp.AddSeconds(1) };
        fixture.Local.Seed(changed, [2]);
        return Selected(fixture, Item, changed);
    }

    private static WorkSelection RewriteAgain(Fixture fixture)
    {
        var newest = LocalEntry(Item, LocalId) with { Modified = Stamp.AddSeconds(2) };
        fixture.Local.Seed(newest, [3]);
        return Seed(fixture, new ChangeNotice(Side.Local, ChangeKind.Changed, Item, null, LocalId, newest, false, false),
            page: 2);
    }

    private static Boundary Validating(Fixture fixture) => new(request =>
    {
        fixture.Inner.ValidateLiveCoverage(request.Selection, request.Coverage, request.Footprint);
        return new Lease();
    });

    private static SelectionPass ValidatedRunner(Fixture fixture, WorkSelection selection, Boundary boundary) =>
        Runner(fixture, selection, boundary, (selected, _, _) => Task.FromResult(Coverage(selected)));
}
