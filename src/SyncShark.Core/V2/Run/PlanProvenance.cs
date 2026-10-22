using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>Opt-in disk-backed observations of production decisions, scoped to the caller's async flow.</summary>
internal sealed class PlanProvenance : IDisposable
{
    internal static readonly AsyncLocal<PlanProvenance?> Current = new();
    private readonly SqliteConnection _db;
    private readonly TextWriter _output;
    private readonly object _gate = new();
    private SqliteTransaction? _transaction;
    private int _pending;
    private int _run;
    private string _scope = "Touched";
    internal PlanProvenance(string database, TextWriter output)
    {
        _output = output;
        using (new FileStream(database, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
        _db.Open();
        Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; CREATE TABLE visibility(side TEXT,path TEXT,hidden INTEGER,reason TEXT,PRIMARY KEY(side,path)) WITHOUT ROWID; CREATE TABLE claims(kind TEXT,path TEXT,PRIMARY KEY(kind,path)) WITHOUT ROWID; CREATE TABLE rejected(kind TEXT,path TEXT,side TEXT,PRIMARY KEY(kind,path,side)) WITHOUT ROWID; CREATE TABLE emitted(id TEXT PRIMARY KEY) WITHOUT ROWID; CREATE TABLE decided(id TEXT PRIMARY KEY,baseline INTEGER,scope TEXT,ha TEXT,ra TEXT,hb TEXT,rb TEXT) WITHOUT ROWID;");
        Row("run", "kind", "rule", "targetSide", "baselinePresent", "sourcePath", "destinationPath", "scope", "hiddenA", "reasonA", "hiddenB", "reasonB");
    }
    private int Execute(string sql, params (string Name, object Value)[] parameters)
    {
        var batch = !sql.StartsWith("PRAGMA", StringComparison.Ordinal);
        if (batch) _transaction ??= _db.BeginTransaction();
        using var command = _db.CreateCommand(); command.CommandText = sql;
        command.Transaction = _transaction;
        foreach (var entry in parameters) command.Parameters.AddWithValue(entry.Name, entry.Value);
        var changed = command.ExecuteNonQuery();
        if (batch && ++_pending >= 5000) Flush();
        return changed;
    }
    private void Flush()
    {
        _transaction?.Commit();
        _transaction?.Dispose();
        _transaction = null;
        _pending = 0;
    }
    internal void BeginRun(int run)
    {
        lock (_gate) { _run = run; _scope = "Touched"; Execute("DELETE FROM visibility; DELETE FROM claims; DELETE FROM rejected; DELETE FROM emitted; DELETE FROM decided;"); }
    }
    internal void BeginScope(string scope)
    {
        lock (_gate) { _scope = scope; Execute("DELETE FROM visibility;"); }
    }
    internal void Visibility(Side side, ItemState item, bool hidden, string reason)
    {
        lock (_gate) Execute("INSERT INTO visibility VALUES($s,$p,$h,$r) ON CONFLICT(side,path) DO UPDATE SET hidden=excluded.hidden,reason=excluded.reason",
            ("$s", side.ToString()), ("$p", PathKey.Of(item.Path)), ("$h", hidden ? 1 : 0), ("$r", reason));
    }
    internal void Claim(string kind, ItemState item)
    {
        lock (_gate) Execute("INSERT OR IGNORE INTO claims VALUES($k,$p)", ("$k", kind), ("$p", item.ObjectId + ":" + PathKey.Of(item.Path)));
    }
    internal void Rejected(SyncAction action, string reason)
    {
        if (action.Kind != ActionKind.CopyNew) return;
        lock (_gate) Execute("INSERT OR IGNORE INTO rejected VALUES($k,$p,$s)", ("$k", reason), ("$p", PathKey.Of(action.Path)), ("$s", action.Target.ToString()));
    }
    private (string Hidden, string Reason) VisibilityAt(Side side, string path)
    {
        using var command = _db.CreateCommand(); command.CommandText = "SELECT hidden,reason FROM visibility WHERE side=$s AND path=$p";
        command.Transaction = _transaction;
        command.Parameters.AddWithValue("$s", side.ToString()); command.Parameters.AddWithValue("$p", PathKey.Of(path));
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetInt32(0) == 0 ? "n" : "y", reader.GetString(1)) : ("n", "not-in-scope");
    }
    internal void Decided(SyncAction action, bool baseline)
    {
        lock (_gate)
        {
            var source = action.Source?.Path ?? action.FromPath ?? action.Path;
            var a = VisibilityAt(Side.A, action.Target == Side.A ? action.Path : source);
            var b = VisibilityAt(Side.B, action.Target == Side.B ? action.Path : source);
            Execute("INSERT OR REPLACE INTO decided VALUES($id,$base,$scope,$ha,$ra,$hb,$rb)",
                ("$id", action.ActionId), ("$base", baseline ? 1 : 0), ("$scope", _scope),
                ("$ha", a.Hidden), ("$ra", a.Reason), ("$hb", b.Hidden), ("$rb", b.Reason));
        }
    }
    internal void Final(Plan plan, IStateStore store)
    {
        lock (_gate)
        {
            foreach (var action in plan.Actions)
            {
                if (Execute("INSERT OR IGNORE INTO emitted VALUES($id)", ("$id", action.ActionId)) == 0) continue;
                var source = action.Source?.Path ?? action.FromPath ?? action.Path;
                var a = VisibilityAt(Side.A, action.Target == Side.A ? action.Path : source);
                var b = VisibilityAt(Side.B, action.Target == Side.B ? action.Path : source);
                var baseline = false;
                var scope = _scope;
                using var details = _db.CreateCommand();
                details.Transaction = _transaction;
                details.CommandText = "SELECT baseline,scope,ha,ra,hb,rb FROM decided WHERE id=$id";
                details.Parameters.AddWithValue("$id", action.ActionId);
                using (var reader = details.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        baseline = reader.GetInt32(0) != 0; scope = reader.GetString(1);
                        a = (reader.GetString(2), reader.GetString(3)); b = (reader.GetString(4), reader.GetString(5));
                    }
                    else baseline = action.ObjectKey is { } key && store.Baselines(plan.PairId, [key]).Count > 0;
                }
                Row(_run.ToString(), action.Kind.ToString(), action.Rule.ToString(), action.Target.ToString(), baseline ? "y" : "n",
                    source, action.Path, scope, a.Hidden, a.Reason, b.Hidden, b.Reason);
            }
            Flush();
            _output.Flush();
        }
    }
    internal void Summary(Action<string> print)
    {
        lock (_gate)
        {
            Flush();
            using var command = _db.CreateCommand();
            command.CommandText = "SELECT 'raw-same-path-pairs',COUNT(*) FROM visibility a JOIN visibility b ON a.path=b.path AND a.side='A' AND b.side='B' UNION ALL SELECT 'both-hidden',COUNT(*) FROM visibility a JOIN visibility b ON a.path=b.path AND a.side='A' AND b.side='B' WHERE a.hidden=1 AND b.hidden=1 UNION ALL SELECT 'one-visible:A:opposite-'||COALESCE(b.reason,'absent-in-raw-scope'),COUNT(*) FROM visibility a LEFT JOIN visibility b ON a.path=b.path AND b.side='B' WHERE a.side='A' AND a.hidden=0 AND (b.path IS NULL OR b.hidden=1) GROUP BY 1 UNION ALL SELECT 'one-visible:B:opposite-'||COALESCE(a.reason,'absent-in-raw-scope'),COUNT(*) FROM visibility b LEFT JOIN visibility a ON a.path=b.path AND a.side='A' WHERE b.side='B' AND b.hidden=0 AND (a.path IS NULL OR a.hidden=1) GROUP BY 1 UNION ALL SELECT 'claims:'||kind,COUNT(*) FROM claims GROUP BY kind UNION ALL SELECT 'CopyNew-rejected:'||kind,COUNT(*) FROM rejected GROUP BY kind";
            using var reader = command.ExecuteReader();
            var counts = new Dictionary<string, long>(StringComparer.Ordinal)
            {
                ["raw-same-path-pairs"] = 0, ["both-hidden"] = 0,
                ["claims:baseline-identity-A"] = 0, ["claims:baseline-identity-B"] = 0,
                ["claims:replacement-path-A"] = 0, ["claims:replacement-path-B"] = 0,
                ["CopyNew-rejected:occupancy"] = 0, ["CopyNew-rejected:protection"] = 0
            };
            while (reader.Read()) counts[reader.GetString(0)] = reader.GetInt64(1);
            foreach (var count in counts) print($"Provenance run {_run}: {count.Key}={count.Value}");
            print($"Provenance run {_run}: one-visible-total={counts.Where(row => row.Key.StartsWith("one-visible:", StringComparison.Ordinal)).Sum(row => row.Value)}");
        }
    }
    private void Row(params string[] values) => _output.WriteLine(string.Join(",", values.Select(V2CliRuntime.CsvField)));
    public void Dispose()
    {
        lock (_gate)
        {
            try { Flush(); }
            finally { _transaction?.Dispose(); _db.Dispose(); }
        }
    }
}
