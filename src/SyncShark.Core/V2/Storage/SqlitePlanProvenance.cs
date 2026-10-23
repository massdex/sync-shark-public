using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

/// <summary>Opt-in disk-backed observations of production decisions, scoped to the caller's async flow.</summary>
internal sealed class SqlitePlanProvenance : PlanProvenance
{
    private readonly SqliteConnection _db;
    private readonly TextWriter _output;
    private readonly object _gate = new();
    private SqliteTransaction? _transaction;
    private int _pending;
    private int _run;
    private string _scope = "Touched";
    internal SqlitePlanProvenance(string database, TextWriter output)
    {
        try
        {
            _output = output;
            using (new FileStream(database, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString());
            _db.Open();
            Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; CREATE TABLE visibility(side TEXT,path TEXT,hidden INTEGER,reason TEXT,PRIMARY KEY(side,path)) WITHOUT ROWID; CREATE TABLE claims(kind TEXT,path TEXT,PRIMARY KEY(kind,path)) WITHOUT ROWID; CREATE TABLE rejected(kind TEXT,path TEXT,side TEXT,PRIMARY KEY(kind,path,side)) WITHOUT ROWID; CREATE TABLE emitted(id TEXT PRIMARY KEY) WITHOUT ROWID; CREATE TABLE decided(id TEXT PRIMARY KEY,baseline INTEGER,scope TEXT,ha TEXT,ra TEXT,hb TEXT,rb TEXT) WITHOUT ROWID;");
            Row("run", "kind", "rule", "targetSide", "baselinePresent", "sourcePath", "destinationPath", "scope", "hiddenA", "reasonA", "hiddenB", "reasonB");

        }
        catch (SqliteException error) when (StoreSchema.IsDamaged(error))
        {
            var failure = new StateDamagedException($"v2 state is damaged: {error.Message}", error);
            SqliteStateStore.DisposePreserving(failure, _transaction, _db);
            throw failure;
        }
        catch (Exception error)
        {
            SqliteStateStore.DisposePreserving(error, _transaction, _db);
            throw;
        }
    }
    private int Execute(string sql, params (string Name, object Value)[] parameters)
    {
        try
        {
            var batch = !sql.StartsWith("PRAGMA", StringComparison.Ordinal);
            if (batch) _transaction ??= _db.BeginTransaction();
            var command = _db.CreateCommand();
            Exception? cleanupPrimary0 = null;
            try
            {
                command.CommandText = sql;
                command.Transaction = _transaction;
                foreach (var entry in parameters) command.Parameters.AddWithValue(entry.Name, entry.Value);
                var changed = command.ExecuteNonQuery();
                if (batch && ++_pending >= 5000) Flush();
                return changed;

            }
            catch (Exception cleanupError) { cleanupPrimary0 = cleanupError; throw; }
            finally { SqliteStateStore.DisposePreserving(cleanupPrimary0, command); }
        }
        catch (SqliteException error) when (StoreSchema.IsDamaged(error))
        { throw new StateDamagedException($"v2 state is damaged: {error.Message}", error); }
    }
    private void Flush()
    {
        try
        {
            _transaction?.Commit();
            _transaction?.Dispose();
            _transaction = null;
            _pending = 0;

        }
        catch (SqliteException error) when (StoreSchema.IsDamaged(error))
        { throw new StateDamagedException($"v2 state is damaged: {error.Message}", error); }
    }
    internal override void BeginRun(int run)
    {
        lock (_gate) { _run = run; _scope = "Touched"; Execute("DELETE FROM visibility; DELETE FROM claims; DELETE FROM rejected; DELETE FROM emitted; DELETE FROM decided;"); }
    }
    internal override void BeginScope(string scope)
    {
        lock (_gate) { _scope = scope; Execute("DELETE FROM visibility;"); }
    }
    internal override void Visibility(Side side, ItemState item, bool hidden, string reason)
    {
        lock (_gate) Execute("INSERT INTO visibility VALUES($s,$p,$h,$r) ON CONFLICT(side,path) DO UPDATE SET hidden=excluded.hidden,reason=excluded.reason",
        ("$s", side.ToString()), ("$p", PathKey.Of(item.Path)), ("$h", hidden ? 1 : 0), ("$r", reason));
    }
    internal override void Claim(string kind, ItemState item)
    {
        lock (_gate) Execute("INSERT OR IGNORE INTO claims VALUES($k,$p)", ("$k", kind), ("$p", item.ObjectId + ":" + PathKey.Of(item.Path)));
    }
    internal override void Rejected(SyncAction action, string reason)
    {
        if (action.Kind != ActionKind.CopyNew) return;
        lock (_gate) Execute("INSERT OR IGNORE INTO rejected VALUES($k,$p,$s)", ("$k", reason), ("$p", PathKey.Of(action.Path)), ("$s", action.Target.ToString()));
    }
    private (string Hidden, string Reason) VisibilityAt(Side side, string path)
    {
        try
        {
            var command = _db.CreateCommand();
            Exception? cleanupPrimary1 = null;
            try
            {
                command.CommandText = "SELECT hidden,reason FROM visibility WHERE side=$s AND path=$p";
                command.Transaction = _transaction;
                command.Parameters.AddWithValue("$s", side.ToString()); command.Parameters.AddWithValue("$p", PathKey.Of(path));
                var reader = command.ExecuteReader();
                Exception? cleanupPrimary2 = null;
                try
                {
                    return reader.Read() ? (reader.GetInt32(0) == 0 ? "n" : "y", reader.GetString(1)) : ("n", "not-in-scope");

                }
                catch (Exception cleanupError) { cleanupPrimary2 = cleanupError; throw; }
                finally { SqliteStateStore.DisposePreserving(cleanupPrimary2, reader); }

            }
            catch (Exception cleanupError) { cleanupPrimary1 = cleanupError; throw; }
            finally { SqliteStateStore.DisposePreserving(cleanupPrimary1, command); }
        }
        catch (SqliteException error) when (StoreSchema.IsDamaged(error))
        { throw new StateDamagedException($"v2 state is damaged: {error.Message}", error); }
    }
    internal override void Decided(SyncAction action, bool baseline)
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
    internal override void Final(Plan plan, IStateStore store)
    {
        try
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
                    var details = _db.CreateCommand();
                    Exception? cleanupPrimary3 = null;
                    try
                    {
                        details.Transaction = _transaction;
                        details.CommandText = "SELECT baseline,scope,ha,ra,hb,rb FROM decided WHERE id=$id";
                        details.Parameters.AddWithValue("$id", action.ActionId);
                        {
                            var reader = details.ExecuteReader();
                            Exception? cleanupPrimary4 = null;
                            try
                            {
                                if (reader.Read())
                                {
                                    baseline = reader.GetInt32(0) != 0; scope = reader.GetString(1);
                                    a = (reader.GetString(2), reader.GetString(3)); b = (reader.GetString(4), reader.GetString(5));
                                }
                                else baseline = action.ObjectKey is { } key && store.Baselines(plan.PairId, [key]).Count > 0;

                            }
                            catch (Exception cleanupError) { cleanupPrimary4 = cleanupError; throw; }
                            finally { SqliteStateStore.DisposePreserving(cleanupPrimary4, reader); }
                        }
                        Row(_run.ToString(), action.Kind.ToString(), action.Rule.ToString(), action.Target.ToString(), baseline ? "y" : "n",
                        source, action.Path, scope, a.Hidden, a.Reason, b.Hidden, b.Reason);

                    }
                    catch (Exception cleanupError) { cleanupPrimary3 = cleanupError; throw; }
                    finally { SqliteStateStore.DisposePreserving(cleanupPrimary3, details); }
                }
                Flush();
                _output.Flush();
            }

        }
        catch (SqliteException error) when (StoreSchema.IsDamaged(error))
        { throw new StateDamagedException($"v2 state is damaged: {error.Message}", error); }
    }
    internal override void Summary(Action<string> print)
    {
        try
        {
            lock (_gate)
            {
                Flush();
                var command = _db.CreateCommand();
                Exception? cleanupPrimary5 = null;
                try
                {
                    command.CommandText = "SELECT 'raw-same-path-pairs',COUNT(*) FROM visibility a JOIN visibility b ON a.path=b.path AND a.side='A' AND b.side='B' UNION ALL SELECT 'both-hidden',COUNT(*) FROM visibility a JOIN visibility b ON a.path=b.path AND a.side='A' AND b.side='B' WHERE a.hidden=1 AND b.hidden=1 UNION ALL SELECT 'one-visible:A:opposite-'||COALESCE(b.reason,'absent-in-raw-scope'),COUNT(*) FROM visibility a LEFT JOIN visibility b ON a.path=b.path AND b.side='B' WHERE a.side='A' AND a.hidden=0 AND (b.path IS NULL OR b.hidden=1) GROUP BY 1 UNION ALL SELECT 'one-visible:B:opposite-'||COALESCE(a.reason,'absent-in-raw-scope'),COUNT(*) FROM visibility b LEFT JOIN visibility a ON a.path=b.path AND a.side='A' WHERE b.side='B' AND b.hidden=0 AND (a.path IS NULL OR a.hidden=1) GROUP BY 1 UNION ALL SELECT 'claims:'||kind,COUNT(*) FROM claims GROUP BY kind UNION ALL SELECT 'CopyNew-rejected:'||kind,COUNT(*) FROM rejected GROUP BY kind";
                    var reader = command.ExecuteReader();
                    Exception? cleanupPrimary6 = null;
                    try
                    {
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
                    catch (Exception cleanupError) { cleanupPrimary6 = cleanupError; throw; }
                    finally { SqliteStateStore.DisposePreserving(cleanupPrimary6, reader); }

                }
                catch (Exception cleanupError) { cleanupPrimary5 = cleanupError; throw; }
                finally { SqliteStateStore.DisposePreserving(cleanupPrimary5, command); }
            }

        }
        catch (SqliteException error) when (StoreSchema.IsDamaged(error))
        { throw new StateDamagedException($"v2 state is damaged: {error.Message}", error); }
    }
    private void Row(params string[] values) => _output.WriteLine(string.Join(",", values.Select(CsvFields.Encode)));
    public override void Dispose()
    {
        lock (_gate)
        {
            Exception? primary = null;
            try { Flush(); }
            catch (Exception error) { primary = error; throw; }
            finally { SqliteStateStore.DisposePreserving(primary, _transaction, _db); }
        }
    }
}
