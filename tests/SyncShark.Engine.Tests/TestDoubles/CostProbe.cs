using Massdex.SyncShark.Engine;
using Massdex.SyncShark.Engine.Storage;
using Massdex.SyncShark.V2;
using SQLitePCL;

namespace Massdex.SyncShark.Engine.Tests.TestDoubles;

/// <summary>Where a cost probe stops reading one statement: after the first row (a scalar read) or after every row.</summary>
internal enum ProbeRead
{
    /// <summary>Steps every row, as a reader loop or a write does.</summary>
    All,

    /// <summary>Steps only the first row, as <c>ExecuteScalar</c> does.</summary>
    First,
}

/// <summary>One statement execution's measured cost.</summary>
/// <param name="PairSourceOccurrences">Ordinary and plan pair-subquery **source occurrences** in the executed
/// text. This is a source count, never an executed count: executed pair counts are unavailable through the
/// current SQLite stack and probe (SQLite 3.53.4 is built without `STMT_SCANSTATUS`; SQLitePCLRaw 3.0.5 exposes no
/// scanstatus binding), so the statement's VM steps are its executed measure.</param>
/// <param name="JsonSourceOccurrences">`json_each(` and `json_extract(` **JSON source occurrences** in the executed
/// text. This is a source count, never an executed count: the function-call count can differ (a correlated extraction
/// runs per row) and is likewise unavailable through the current SQLite stack and probe, so the statement's VM steps
/// are its executed measure.</param>
/// <param name="ProbePrepares">Statements this probe prepared; it is not a production prepare count.</param>
/// <param name="DepthCalls">Executed plan-depth callbacks, including index evaluation.</param>
internal readonly record struct StatementCost(long VmSteps, long PathKeyCalls, long PairSourceOccurrences,
    long JsonSourceOccurrences, long ChangedRows, int ProbePrepares, long DepthCalls = 0, long IssueFactCalls = 0)
{
    public static StatementCost operator +(StatementCost left, StatementCost right) => new(
        left.VmSteps + right.VmSteps, left.PathKeyCalls + right.PathKeyCalls,
        left.PairSourceOccurrences + right.PairSourceOccurrences,
        left.JsonSourceOccurrences + right.JsonSourceOccurrences,
        left.ChangedRows + right.ChangedRows, left.ProbePrepares + right.ProbePrepares,
        left.DepthCalls + right.DepthCalls, left.IssueFactCalls + right.IssueFactCalls);
}

/// <summary>Section 9.1 test-only cost probe: per-statement VM steps, registered function calls, pair/JSON source
/// occurrences and rows changed.</summary>
/// <remarks>Microsoft.Data.Sqlite keeps its statement handles private and offers no hook, so the probe runs the
/// engine's own SQL text, schema and bound values through its own raw SQLitePCL connection. VM steps and function
/// calls are then what the same statement costs on the store's connection; the SQL text and bound values are the
/// engine's constants, not a copy. The schema's function callbacks are registered here (path key, plan depth, the
/// dormant mapped spelling used by the upgrade, and the item-retry cause predicate) so every engine statement and
/// the schema-5 upgrade prepare on this connection. Each statement handle is finalised after its status is read,
/// and any step result other than <c>SQLITE_ROW</c> or <c>SQLITE_DONE</c> fails the probe.</remarks>
internal sealed class CostProbe : IDisposable
{
    private const string PairSubquery = "(SELECT id FROM pair WHERE pair_id = $p)";
    private const string PlanPairSubquery = "(SELECT id FROM durable_state.pair WHERE pair_id=$p)";
    private readonly sqlite3 _database;
    private readonly delegate_function_scalar _pathKey;
    private readonly delegate_function_scalar _historyKey;
    private readonly delegate_function_scalar _depth;
    private readonly delegate_function_scalar _mappedSpelling;
    private readonly delegate_function_scalar _localAccessRetryCause;
    private long _pathKeyCalls;
    private long _depthCalls;
    private long _issueFactCalls;
    private readonly delegate_function_scalar _issueFactsTouched;

    /// <summary>Opens (creating the file if absent) a probe connection and registers a counting path-key function.</summary>
    public CostProbe(string path)
    {
        Batteries_V2.Init();
        if (raw.sqlite3_open(path, out _database) != raw.SQLITE_OK)
            throw new InvalidOperationException("the cost probe could not open " + path);
        _pathKey = CountPathKey;
        _depth = CountDepth;
        _mappedSpelling = CountMappedSpelling;
        _localAccessRetryCause = CountLocalAccessRetryCause;
        _historyKey = (context, _, values) =>
        {
            Interlocked.Increment(ref _pathKeyCalls);
            raw.sqlite3_result_text(context, PathNames.DropboxKey(raw.sqlite3_value_text(values[0]).utf8_to_string()));
        };
        Register(StateSchema.HistoryKeyFunction, _historyKey);
        Register(StateSchema.PathKeyFunction, _pathKey);
        Register(StateSchema.PlanDepthFunction, _depth);
        Register(StateSchema.MappedSpellingFunction, _mappedSpelling);
        Register(StateSchema.LocalAccessRetryCauseFunction, _localAccessRetryCause);
        _issueFactsTouched = (context, _, _) =>
        {
            Interlocked.Increment(ref _issueFactCalls);
            raw.sqlite3_result_int(context, 0);
        };
        var registered = raw.sqlite3_create_function(_database, StateSchema.IssueFactsTouchedFunction, 2,
            raw.SQLITE_UTF8, null, _issueFactsTouched);
        if (registered != raw.SQLITE_OK) throw Error("register issue facts", registered);
    }

    /// <summary>Runs the engine's schema without measuring it: the schema-4 base plus the one schema-5 addition pass.</summary>
    public void CreateSchema()
    {
        Execute(StateSchema.Pragmas);
        Execute(StateSchema.Create);
        Execute(StateSchema.SchemaAdditions);
        Execute(StateSchema.DeletedHistorySchema + StateSchema.RebuildIssues + StateSchema.DeletedHistoryBlocks + StateSchema.DropDeletedHistoryNames + StateSchema.SpentDeletedHistory + StateSchema.LocalPublishedColumns + StateSchema.DiscoveryRequiredColumn + StateSchema.RenameContinuationColumns);
        Execute(StateSchema.PublishVersion);
        Execute(StateSchema.CreateIssueFactsTriggers);
    }

    /// <summary>Attaches a scan database and creates its schema, then returns the probe for chaining.</summary>
    public CostProbe AttachScan(string path)
    {
        Execute(StateSchema.AttachScan, (StateSchema.ParamPath, path));
        Execute(StateSchema.ScanPragmas);
        Execute(StateSchema.CreateScan);
        return this;
    }

    /// <summary>Mirrors OpenFolderPlan on a caller-supplied root (use <c>:memory:</c>) and a caller-deleted plan path.</summary>
    public CostProbe AttachPlan(string statePath, string planPath)
    {
        Execute(StateSchema.AttachState, (StateSchema.ParamPath, statePath));
        Execute(StateSchema.AttachScan, (StateSchema.ParamPath, planPath));
        Execute(StateSchema.PlanPragmas);
        Execute(StateSchema.CreateScan);
        Execute(StateSchema.PlanCreateClassified);
        Execute(StateSchema.PlanCreateClassifiedLocalRoots);
        return this;
    }

    /// <summary>Opens a write transaction, so several measured statements report one commit.</summary>
    public void Begin() => Execute("BEGIN;");

    /// <summary>Commits the write transaction opened by <see cref="Begin"/>.</summary>
    public void Commit() => Execute("COMMIT;");

    /// <summary>Runs one statement (or every statement in the text) to completion.</summary>
    public StatementCost Execute(string sql, params (string Name, object? Value)[] arguments) =>
        Run(sql, ProbeRead.All, arguments);

    /// <summary>Runs a scalar read: steps only the first row, as <c>ExecuteScalar</c> does.</summary>
    public StatementCost ExecuteFirst(string sql, params (string Name, object? Value)[] arguments) =>
        Run(sql, ProbeRead.First, arguments);

    public void Dispose()
    {
        raw.sqlite3_close(_database);
    }

    private void Register(string name, delegate_function_scalar function)
    {
        var rc = raw.sqlite3_create_function(_database, name, 1,
            raw.SQLITE_UTF8 | raw.SQLITE_DETERMINISTIC, null, function);
        if (rc != raw.SQLITE_OK) throw Error("register " + name, rc);
    }

    private StatementCost Run(string sql, ProbeRead read, (string Name, object? Value)[] arguments)
    {
        var cost = new StatementCost();
        var rest = sql;
        while (!string.IsNullOrWhiteSpace(rest))
        {
            var pathKeyBefore = _pathKeyCalls;
            var depthBefore = _depthCalls;
            var issueFactsBefore = _issueFactCalls;
            var changedBefore = raw.sqlite3_total_changes(_database);
            var rc = raw.sqlite3_prepare_v2(_database, rest, out var statement, out var tail);
            var consumed = rest[..(rest.Length - (tail?.Length ?? 0))];
            rest = tail ?? "";
            if (rc != raw.SQLITE_OK) throw Error("prepare", rc);
            if (statement is null) continue;
            try
            {
                foreach (var (name, value) in arguments) Bind(statement, name, value);
                CheckBound(statement, arguments);
                while (true)
                {
                    var step = raw.sqlite3_step(statement);
                    if (step == raw.SQLITE_ROW)
                    {
                        // A scalar read's intended stop is the first row; otherwise every row is stepped.
                        if (read == ProbeRead.First) break;
                        continue;
                    }
                    if (step == raw.SQLITE_DONE) break;
                    throw Error("step", step);
                }
                // shortcut: sqlite3_stmt_status returns 32 bits, so a statement above 2^31 VM steps would wrap;
                // change the probe before any measured workload can approach that counter's range.
                cost += new StatementCost(
                    raw.sqlite3_stmt_status(statement, raw.SQLITE_STMTSTATUS_VM_STEP, 0),
                    Interlocked.Read(ref _pathKeyCalls) - pathKeyBefore,
                    CountOccurrences(consumed, PairSubquery) + CountOccurrences(consumed, PlanPairSubquery),
                    CountOccurrences(consumed, "json_each(") + CountOccurrences(consumed, "json_extract("),
                    raw.sqlite3_total_changes(_database) - changedBefore, 1,
                    Interlocked.Read(ref _depthCalls) - depthBefore,
                    Interlocked.Read(ref _issueFactCalls) - issueFactsBefore);
            }
            finally
            {
                raw.sqlite3_finalize(statement);
            }
        }
        return cost;
    }

    private void CountPathKey(sqlite3_context context, object? userData, sqlite3_value[] values)
    {
        Interlocked.Increment(ref _pathKeyCalls);
        if (raw.sqlite3_value_type(values[0]) == raw.SQLITE_NULL) raw.sqlite3_result_null(context);
        else raw.sqlite3_result_text(context,
            PathNames.Key(raw.sqlite3_value_text(values[0]).utf8_to_string()));
    }

    private void CountDepth(sqlite3_context context, object? userData, sqlite3_value[] values)
    {
        Interlocked.Increment(ref _depthCalls);
        raw.sqlite3_result_int(context, PathNames.Depth(raw.sqlite3_value_text(values[0]).utf8_to_string()));
    }

    private void CountMappedSpelling(sqlite3_context context, object? userData, sqlite3_value[] values)
    {
        if (raw.sqlite3_value_type(values[0]) == raw.SQLITE_NULL) raw.sqlite3_result_null(context);
        else raw.sqlite3_result_text(context,
            DropboxNameMapping.Map(raw.sqlite3_value_text(values[0]).utf8_to_string()));
    }

    // The upgrade schema and the item-retry statements call this predicate. IssuePolicy owns the family rule; the
    // probe mirrors Storage's registration so those statements prepare on the raw connection.
    private void CountLocalAccessRetryCause(sqlite3_context context, object? userData, sqlite3_value[] values)
    {
        var family = raw.sqlite3_value_type(values[0]) == raw.SQLITE_TEXT &&
            raw.sqlite3_value_text(values[0]).utf8_to_string() is
                nameof(TextKey.FailLocalInUse) or nameof(TextKey.FailLocalInUseOrAccessDenied);
        raw.sqlite3_result_int(context, family ? 1 : 0);
    }

    private void Bind(sqlite3_stmt statement, string name, object? value)
    {
        var index = raw.sqlite3_bind_parameter_index(statement, name);
        if (index == 0) return;
        var rc = value switch
        {
            null or DBNull => raw.sqlite3_bind_null(statement, index),
            string text => raw.sqlite3_bind_text(statement, index, text),
            bool flag => raw.sqlite3_bind_int64(statement, index, flag ? 1 : 0),
            long number => raw.sqlite3_bind_int64(statement, index, number),
            int number => raw.sqlite3_bind_int64(statement, index, number),
            double number => raw.sqlite3_bind_double(statement, index, number),
            byte[] bytes => raw.sqlite3_bind_blob(statement, index, bytes),
            _ => throw new ArgumentException("the cost probe cannot bind " + value.GetType(), nameof(value)),
        };
        if (rc != raw.SQLITE_OK) throw Error("bind " + name, rc);
    }

    // An unbound declared parameter keeps its null value and silently changes the measured shape, so a message that
    // omits one of the statement's parameters fails here instead of running.
    private static void CheckBound(sqlite3_stmt statement, (string Name, object? Value)[] arguments)
    {
        var count = raw.sqlite3_bind_parameter_count(statement);
        for (var index = 1; index <= count; index++)
        {
            var name = raw.sqlite3_bind_parameter_name(statement, index).utf8_to_string();
            if (name is null)
                throw new InvalidOperationException("the cost probe cannot bind unnamed parameter " + index +
                    "; every statement parameter must be named");
            var bound = false;
            foreach (var argument in arguments)
                if (argument.Name == name)
                {
                    bound = true;
                    break;
                }
            if (!bound)
                throw new InvalidOperationException(
                    "the cost probe left " + name + " unbound; every declared parameter needs a value");
        }
    }

    private Exception Error(string what, int code) =>
        new InvalidOperationException("cost probe " + what + " failed: " + code + " " +
            raw.sqlite3_errmsg(_database).utf8_to_string());

    private static long CountOccurrences(string text, string value)
    {
        long count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
