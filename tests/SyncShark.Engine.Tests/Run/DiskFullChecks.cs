using System.ComponentModel;
using System.Text;
using Massdex.SyncShark.Engine.Executor;
using Massdex.SyncShark.Engine.Local;
using Massdex.SyncShark.Engine.Run;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.Engine.Tests.TestDoubles;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Tests.Run;

/// <summary>#36 disk-full behaviour (`--engine-checks disk-full`): the shared classifier and carrier, joins that
/// keep a disk-full primary, real SQLite FULL, scan admission, the log's own failure, committed completion's
/// secondary failure and owed history inside the completion transaction.</summary>
public static class DiskFullChecks
{
    // Windows HRESULT_FROM_WIN32(ERROR_DISK_FULL), as IOException reports a full volume.
    private const int DiskFullHResult = unchecked((int)0x80070070);
    private static readonly object WriteLock = new();

    public static void Run()
    {
        ClassifierCases();
        ScopeCases().GetAwaiter().GetResult();
        JoinCases().GetAwaiter().GetResult();
        WithFolder(folder =>
        {
            using var connection = OpenScanConnection(Path.Combine(folder, "scan.db"));
            SqliteFullCases(connection);
            AdmissionCases(connection, folder);
        });
        WithFolder(folder => ProvisionalFullAsync(folder).GetAwaiter().GetResult());
        WithFolder(folder => EarlierLogFullAsync(folder).GetAwaiter().GetResult());
        WithFolder(folder => SecondaryFailureAsync(folder).GetAwaiter().GetResult());
        WithFolder(folder => OwedHistoryAsync(folder).GetAwaiter().GetResult());
    }

    private static IOException Full() => new("full", DiskFullHResult);

    private static void ClassifierCases()
    {
        var full = Full();
        Check.Require(ReferenceEquals(ResourceFailures.Find(new AggregateException(new InvalidOperationException(), full)), full),
            "a disk-full later aggregate member is found, not only the first");
        Check.Require(ReferenceEquals(ResourceFailures.Find(new InventoryIncompleteException("x", full)), full),
            "a disk-full under an incomplete listing is found");
        Check.Require(ResourceFailures.Find(new Win32Exception(112)) is not null, "ERROR_DISK_FULL is disk-full");
        Check.Require(ResourceFailures.Find(new InvalidOperationException()) is null && ResourceFailures.Find(null) is null,
            "other failures are not disk-full");
        var genuine = new InvalidOperationException();
        Check.Require(ReferenceEquals(ResourceFailures.Prefer(null, full), full) &&
            ReferenceEquals(ResourceFailures.Prefer(new OperationCanceledException(), full), full) &&
            ReferenceEquals(ResourceFailures.Prefer(new InventoryIncompleteException("x", null), full), full),
            "disk-full outranks none, cancellation and an incomplete listing");
        Check.Require(ReferenceEquals(ResourceFailures.Prefer(genuine, full), genuine) &&
            ReferenceEquals(ResourceFailures.Prefer(genuine, null), genuine), "an earlier genuine primary is kept");
    }

    private static async Task ScopeCases()
    {
        var full = Full();
        RepairScope.Record(full);
        Check.Require(RepairScope.Recorded is null, "outside a Repair nothing is recorded");
        using (RepairScope.Enter())
        {
            RepairScope.Record(new InvalidOperationException());
            Check.Require(RepairScope.Recorded is null, "only disk-full is recorded");
            await Task.Run(() => RepairScope.Record(full));
            Check.Require(ReferenceEquals(RepairScope.Recorded, full), "work the Repair awaits records on its scope");
            RepairScope.Record(Full());
            Check.Require(ReferenceEquals(RepairScope.Recorded, full), "the first disk-full is kept");
            using (RepairScope.Enter()) Check.Require(RepairScope.Recorded is null, "a nested Repair has its own scope");
            Check.Require(ReferenceEquals(RepairScope.Recorded, full), "disposal restores the previous scope");
            Task<Exception?> detached;
            using (ExecutionContext.SuppressFlow()) detached = Task.Run(() => RepairScope.Recorded);
            Check.Require(await detached is null,
                "work started under suppressed flow, as EngineHost.WithoutFlow starts the journal, has no Repair scope");
        }
        Check.Require(RepairScope.Recorded is null, "the scope ends with its Repair");
        using (RepairScope.Enter())
        {
            Check.Require(LocalErrors.Cause(full, TextKey.RecycleFailed) == TextKey.FailLocalNoSpace &&
                ReferenceEquals(RepairScope.Recorded, full),
                "a converted local disk-full still reaches the Repair through the shared mapping");
        }
    }

    private static async Task JoinCases()
    {
        foreach (var primary in new Exception?[] { null, new InventoryIncompleteException("x", null), new InvalidOperationException() })
        {
            var full = Full();
            var tasks = new TaskSet(new CancellationTokenSource());
            tasks.Add(Task.FromException(full));
            var joined = await tasks.JoinAsync(primary, cancelSiblings: true, _ => { }, ignoreJoinedFailure: true);
            var expected = primary is InvalidOperationException ? primary : full;
            Check.Require(ReferenceEquals(joined, expected),
                $"an ignored join keeps a member's disk-full over {primary?.GetType().Name ?? "no primary"}");
        }
    }

    private static void SqliteFullCases(SqliteConnection connection)
    {
        const string pair = "full";
        AddPair(connection, pair);
        Execute(connection, "PRAGMA scan_state.max_page_count = " + Number(connection, "PRAGMA scan_state.page_count;") + ";");
        var writer = new ScanWriter(connection, WriteLock, pair, Side.Local, static () => { }, ScanTestSettings.BatchRows);
        Exception? failure = null;
        try
        {
            for (var index = 0; index < ScanTestSettings.BatchRows * 4; index++)
                writer.AddAsync(new Listed(new string('a', 512) + index, Entry(index)), default).GetAwaiter().GetResult();
            writer.CompleteAsync().GetAwaiter().GetResult();
        }
        catch (Exception error) { failure = error; }
        finally { Execute(connection, "PRAGMA scan_state.max_page_count = 1073741823;"); }
        Check.Require(failure is InsufficientSpaceException
            { InnerException: SqliteException { SqliteErrorCode: SQLitePCL.raw.SQLITE_FULL } },
            "a real SQLite FULL leaves Storage as the shared disk-full exception with its cause inner");
    }

    private static void AdmissionCases(SqliteConnection connection, string folder)
    {
        const string pair = "admitted";
        AddPair(connection, pair);
        var space = new RecordingSpace();
        List<Listed> batch = [new("a.txt", Entry(1)), new("b.txt", Entry(2)), new("gone", null)];
        var writer = new ScanWriter(connection, WriteLock, pair, Side.Local, static () => { }, ScanTestSettings.BatchRows,
            space: space, directory: folder);
        foreach (var item in batch) writer.AddAsync(item, default).GetAwaiter().GetResult();
        writer.CompleteAsync().GetAwaiter().GetResult();
        Check.Require(space.Grants.Count == 1 && space.Grants[0] == (folder, ScanEntries.InputBytes(batch)) &&
            space.Retired == 1, "one batch is admitted for its own input bytes and the grant retires");
        Check.Require(Rows(connection, pair) == 2, "the admitted batch is saved");

        const string refused = "refused";
        AddPair(connection, refused);
        var blocked = new ScanWriter(connection, WriteLock, refused, Side.Local, static () => { },
            ScanTestSettings.BatchRows, space: new RecordingSpace { Refuse = true }, directory: folder);
        Exception? failure = null;
        try
        {
            blocked.AddAsync(new Listed("c.txt", Entry(3)), default).GetAwaiter().GetResult();
            blocked.CompleteAsync().GetAwaiter().GetResult();
        }
        catch (Exception error) { failure = error; }
        Check.Require(failure is InsufficientSpaceException && Rows(connection, refused) == 0,
            "a refused batch writes nothing and stops with the disk-full exception");
    }

    // The provisional write hits a full volume: Repair exits with the disk-full failure and saves no summary.
    private static async Task ProvisionalFullAsync(string folder)
    {
        await using var rig = await RepairChecks.ComposeRigAsync(folder);
        using var log = rig.Log(folder, "provisional-full");
        FailLog(log, RunLog.ProvisionalKind);
        var failure = await Failure(() => rig.RunAsync(log, CancellationToken.None));
        Check.Require(failure is InsufficientSpaceException, "a full log at provisional delivery stops Repair as disk-full");
        Check.Require(rig.Store.LastRepair(rig.Pair.PairId) is not { Completed: true } &&
            !rig.Store.FirstRepairDone(rig.Pair.PairId) && rig.Store.Obligated(rig.Pair.PairId),
            "no authoritative completion is published and the obligation remains");
    }

    // An earlier write, such as a Monitor quiet-status line, records the full volume; later writes are skipped,
    // and Repair still stops from the log's stored exception before any summary is saved.
    private static async Task EarlierLogFullAsync(string folder)
    {
        await using var rig = await RepairChecks.ComposeRigAsync(folder);
        using var log = rig.Log(folder, "earlier-full");
        FailLog(log, nameof(RunEventKind.Stage));
        var failure = await Failure(() => rig.RunAsync(log, CancellationToken.None));
        Check.Require(failure is InsufficientSpaceException && log.Failure is IOException { HResult: DiskFullHResult },
            "the log keeps its first disk-full failure and Repair reads it at its boundary");
        Check.Require(rig.Store.LastRepair(rig.Pair.PairId) is null && rig.Store.Obligated(rig.Pair.PairId),
            "an earlier log disk-full saves no summary and keeps the obligation");
    }

    private static async Task SecondaryFailureAsync(string folder)
    {
        await using var rig = await RepairChecks.ComposeRigAsync(folder, decorate: inner => new HookStore(inner)
        {
            FinishSecondary = new IOException("maintenance")
        });
        using var log = rig.Log(folder, "secondary");
        var summary = await rig.RunAsync(log, CancellationToken.None);
        Check.Require(summary.Completed && rig.Store.LastRepair(rig.Pair.PairId) is { Completed: true },
            "a postcommit failure never revokes the committed completion");
        Check.Require(File.ReadLines(log.Path).Any(line => line.Contains(nameof(TextKey.RepairCompletedSecondaryFailure),
            StringComparison.Ordinal)), "the postcommit failure is reported as secondary");
    }

    // History left owed at completion is obligated in the same commit, without Repair's later handoff.
    private static async Task OwedHistoryAsync(string folder)
    {
        var database = Path.Combine(folder, "composed.db");
        await using var rig = await RepairChecks.ComposeRigAsync(folder, decorate: inner => new HookStore(inner)
        {
            BeforeFinishRepair = () =>
            {
                using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                    { DataSource = database, Pooling = false }.ToString());
                StateStore.OpenConnection(connection);
                Execute(connection, "UPDATE pair SET deleted_history_owed=1;");
            }
        });
        using var log = rig.Log(folder, "owed");
        var summary = await rig.RunAsync(log, CancellationToken.None);
        Check.Require(summary.Completed && rig.Store.DeletedHistoryOwed(rig.Pair.PairId) &&
            rig.Store.Obligated(rig.Pair.PairId),
            "completion with history owed commits the acquisition obligation in the same transaction");
    }

    private static void FailLog(RunLog log, string kind)
    {
        var field = typeof(RunLog).GetField("_writer",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        field.SetValue(log, new DiskFullWriter((TextWriter)field.GetValue(log)!, kind));
    }

    private static async Task<Exception?> Failure(Func<Task> run)
    {
        try { await run(); }
        catch (Exception error) { return error; }
        return null;
    }

    private static Entry Entry(long size) =>
        new("e" + size, false, size, DateTimeOffset.UnixEpoch, "id-" + size, "", "", null);

    private static SqliteConnection OpenScanConnection(string path)
    {
        var connection = new SqliteConnection($"Data Source={path}");
        StateStore.OpenConnection(connection);
        Execute(connection, StateSchema.Pragmas);
        Execute(connection, StateSchema.Create + StateSchema.SchemaAdditions + StateSchema.DeletedHistorySchema +
            StateSchema.RebuildIssues + StateSchema.DeletedHistoryBlocks + StateSchema.DropDeletedHistoryNames +
            StateSchema.SpentDeletedHistory + StateSchema.LocalPublishedColumns + StateSchema.DiscoveryRequiredColumn +
            StateSchema.IssuePathKeyIndex + StateSchema.RenameContinuationColumns);
        Execute(connection, StateSchema.AttachScan, (StateSchema.ParamPath, StateSchema.EmptyScan));
        Execute(connection, StateSchema.ScanPragmas);
        Execute(connection, StateSchema.CreateScan);
        return connection;
    }

    private static void AddPair(SqliteConnection connection, string pairId) =>
        Execute(connection, StateSchema.UpsertPair, (StateSchema.ParamPair, pairId), (StateSchema.ParamDef, "{}"));

    private static long Rows(SqliteConnection connection, string pairId) => Number(connection,
        "SELECT count(*) FROM scan_state.scan s JOIN pair p ON p.id=s.pair_id WHERE p.pair_id='" + pairId + "';");

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] arguments)
    {
        using var command = StorageCommand.Create(connection, null, sql, arguments);
        command.ExecuteNonQuery();
    }

    private static long Number(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void WithFolder(Action<string> body)
    {
        var folder = Path.Combine(Path.GetTempPath(), "syncshark-diskfull-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try { body(folder); }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, recursive: true);
        }
    }

    private sealed class RecordingSpace : IWriteSpace
    {
        public List<(string Directory, long Bytes)> Grants { get; } = [];
        public int Retired;
        public bool Refuse { get; init; }

        public ValueTask<IAsyncDisposable> ReserveAsync(string directory, long bytes, CancellationToken ct)
        {
            if (Refuse) throw new InsufficientSpaceException(new IOException("refused", DiskFullHResult));
            Grants.Add((directory, bytes));
            return ValueTask.FromResult<IAsyncDisposable>(new Grant(this));
        }

        private sealed class Grant(RecordingSpace owner) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                owner.Retired++;
                return ValueTask.CompletedTask;
            }
        }
    }

    // A log writer whose first line of the given kind reports a full volume.
    private sealed class DiskFullWriter(TextWriter inner, string kind) : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;

        public override void WriteLine(string? value)
        {
            var fields = value?.Split(RunLog.FieldSeparator) ?? [];
            if (fields.Length > 1 && fields[1] == kind) throw new IOException("full", DiskFullHResult);
            inner.WriteLine(value);
        }

        public override void Flush() => inner.Flush();

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
