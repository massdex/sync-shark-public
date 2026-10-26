using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.V2;

public sealed partial class SqliteStateStore
{
    // Only a prefilter. Run applies its complete protection composition and the Planner decides.
    // Each alias must have an equal counterpart at the same spelling; renamed/mapped work uses
    // ordinary intake. Equal alias counts prevent an unseen extra counterpart alias being admitted.
    private static string SettleSideSql(string side, string other, string root, string opposite) => $$"""
        SELECT 1 WHERE d.root_no={{root}} AND EXISTS(SELECT 1 FROM file_state f INDEXED BY fs_obj
          WHERE f.root_no=d.root_no AND f.object_id=d.object_id)
        AND NOT EXISTS(SELECT 1 FROM file_state f INDEXED BY fs_obj
          WHERE f.root_no=d.root_no AND f.object_id=d.object_id AND NOT (
            f.kind=0 AND f.hash IS NOT NULL AND NOT ({{ProtectionHeldSqlFor("f")}})
            AND NOT EXISTS(SELECT 1 FROM local_rehash h WHERE h.root_no=f.root_no AND h.object_id=f.object_id)
            AND EXISTS(SELECT 1 FROM file_state g
              WHERE g.root_no={{opposite}} AND g.dir_no=f.dir_no AND g.name_key=f.name_key AND g.name=f.name
                AND g.kind=0 AND g.hash=f.hash AND g.size=f.size
                AND NOT ({{ProtectionHeldSqlFor("g")}})
                AND NOT EXISTS(SELECT 1 FROM local_rehash h WHERE h.root_no=g.root_no AND h.object_id=g.object_id)
                AND (SELECT COUNT(*) FROM file_state af INDEXED BY fs_obj
                  WHERE af.root_no=f.root_no AND af.object_id=f.object_id)=
                    (SELECT COUNT(*) FROM file_state ag INDEXED BY fs_obj
                      WHERE ag.root_no=g.root_no AND ag.object_id=g.object_id)
                AND (EXISTS(SELECT 1 FROM sync_point s INDEXED BY sp_{{side}}_obj
                  WHERE s.pair_no=d.pair_no AND s.{{side}}_object_id=f.object_id
                    AND s.{{other}}_object_id=g.object_id AND s.kind=0 AND s.hash=f.hash AND s.size=f.size
                    AND COALESCE(s.{{side}}_dir,s.a_dir)=f.dir_no AND COALESCE(s.{{side}}_name,s.a_name)=f.name
                    AND COALESCE(s.{{other}}_dir,s.a_dir)=g.dir_no AND COALESCE(s.{{other}}_name,s.a_name)=g.name
                    AND s.{{side}}_version_id IS f.version_id AND s.{{side}}_mtime=f.mtime
                    AND s.{{other}}_version_id IS g.version_id AND s.{{other}}_mtime=g.mtime)
                  OR (NOT EXISTS(SELECT 1 FROM sync_point s INDEXED BY sp_{{side}}_obj
                        WHERE s.pair_no=d.pair_no AND s.{{side}}_object_id=f.object_id)
                    AND NOT EXISTS(SELECT 1 FROM sync_point s INDEXED BY sp_{{other}}_obj
                        WHERE s.pair_no=d.pair_no AND s.{{other}}_object_id=g.object_id))))))
        """;

    internal const int SettleExaminationCap = V2Settings.SettleExaminationCap;
    internal int SettleExaminedRows { get; private set; }
    private sealed record SettleCursor(long Version = 0, long Root = -1, string ObjectId = "");

    internal static readonly string SettlePlanningCandidatesSql = """
        WITH window AS MATERIALIZED (
          SELECT * FROM planning_deferred INDEXED BY planning_deferred_pair_version
          WHERE pair_no=$p AND version<$before AND (version,root_no,object_id)>($v,$r,$o)
          ORDER BY version,root_no,object_id LIMIT $scan)
        SELECT d.root_no,d.object_id,d.path,d.version,r.root_id,d.reason,
          d.reason=$observation AND d.alias_dir=-1 AND d.alias_unsettled=0
          AND NOT EXISTS(SELECT 1 FROM local_gap WHERE root_no IN ($a,$b) AND discovery_done=0)
          AND (EXISTS (
        """ + SettleSideSql("a", "b", "$a", "$b") + ")" + "OR EXISTS (" + SettleSideSql("b", "a", "$b", "$a") + ")) " +
        "FROM window d JOIN root r ON r.root_no=d.root_no ORDER BY d.version,d.root_no,d.object_id;";

    internal IReadOnlyList<DeferredPlanning> SettlePlanningCandidates(string pairId, long beforeVersion,
        int remainingPaths, IReadOnlyList<DeferredPlanning> ordinary, bool advance = true) => InStore(() =>
    {
        var pair = Pair(pairId);
        var number = PairNo(pairId);
        if (pair is null || number is null || remainingPaths <= 0) return (IReadOnlyList<DeferredPlanning>)[];
        using var tx = _connection.BeginTransaction();
        var saved = StoreSchema.Scalar(_connection, tx,
            "SELECT json_extract(def_json,'$.PlanningSettleCursor') FROM pair WHERE pair_no=$p;", ("$p", number.Value)) as string;
        var cursor = saved is null ? new SettleCursor() : System.Text.Json.JsonSerializer.Deserialize<SettleCursor>(saved)!;
        var rows = new List<DeferredPlanning>();
        SettleExaminedRows = default;
        // An empty suffix wraps without spending the examination budget. The materialized window
        // fences eligibility work before any correlated probes; even zero matches cost at most one page.
        var complete = true;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            (cursor, complete) = ReadSettleWindow(tx, pair, number.Value, beforeVersion, remainingPaths, cursor, rows);
            if (SettleExaminedRows != 0) break;
            cursor = new();
        }
        // A window cut short by the candidate cap keeps its cursor so eligible rows past the cap are
        // reached on the next run; only an exhausted short window restarts the lap from the beginning.
        if (advance) StoreSchema.Execute(_connection, tx,
            "UPDATE pair SET def_json=json_set(def_json,'$.PlanningSettleCursor',json($cursor)) WHERE pair_no=$p;",
            ("$cursor", System.Text.Json.JsonSerializer.Serialize(
                complete && SettleExaminedRows < SettleExaminationCap ? new SettleCursor() : cursor)), ("$p", number.Value));
        var admitted = MaterializeSettlePage(tx, rows, ordinary, remainingPaths);
        tx.Commit();
        return admitted;
    });

    private List<DeferredPlanning> MaterializeSettlePage(SqliteTransaction tx, List<DeferredPlanning> rows,
        IReadOnlyList<DeferredPlanning> ordinary, int remainingPaths)
    {
        var excluded = ordinary.Select(row => (row.Root, row.ObjectId)).ToHashSet();
        var admitted = new List<DeferredPlanning>();
        foreach (var row in rows)
        {
            if (remainingPaths <= 0) break;
            if (excluded.Contains((row.Root, row.ObjectId))) continue;
            // Materialization initially counts the queued spelling separately from its alias.
            // Allow that one duplicate, then enforce the budget on the deduplicated paths.
            var materialized = MaterializeAliases(tx, row, remainingPaths + 1);
            if (!materialized.AllAliases || materialized.Paths.Count > remainingPaths) continue;
            remainingPaths -= materialized.Paths.Count;
            admitted.Add(materialized);
        }
        return admitted;
    }

    private (SettleCursor Cursor, bool Complete) ReadSettleWindow(SqliteTransaction tx, PairDef pair, long number,
        long beforeVersion, int remainingPaths, SettleCursor cursor, List<DeferredPlanning> rows)
    {
        var limit = Math.Min(SettleCandidateCap, remainingPaths);
        using (var command = StoreSchema.Command(_connection, tx, SettlePlanningCandidatesSql))
        {
            command.Parameters.AddWithValue("$p", number);
            command.Parameters.AddWithValue("$a", RootNo(pair.A.RootId));
            command.Parameters.AddWithValue("$b", RootNo(pair.B.RootId));
            command.Parameters.AddWithValue("$before", beforeVersion);
            command.Parameters.AddWithValue("$observation", ObservationPlanningReason);
            command.Parameters.AddWithValue("$scan", SettleExaminationCap);
            command.Parameters.AddWithValue("$v", cursor.Version);
            command.Parameters.AddWithValue("$r", cursor.Root);
            command.Parameters.AddWithValue("$o", cursor.ObjectId);
            command.Parameters.AddWithValue("$flags", (long)ItemFlags.NeedsRehash);
            command.Parameters.AddWithValue("$gap", 1);
            command.Parameters.AddWithValue("$done", 1);
            command.Parameters.AddWithValue("$after", "");
            command.Parameters.AddWithValue("$observed", "[]");
            using var reader = command.ExecuteReader();
            var complete = true;
            while (reader.Read())
            {
                // Stop reading once the per-run candidate budget is full; the cursor stays on the last
                // admitted row, so the next run resumes at the first eligible row left in this window.
                if (rows.Count >= limit) { complete = false; break; }
                SettleExaminedRows++;
                cursor = new(reader.GetInt64(3), reader.GetInt64(0), reader.GetString(1));
                if (reader.GetBoolean(6))
                    rows.Add(new(cursor.Root, cursor.ObjectId, reader.GetString(2), cursor.Version,
                        [], true, reader.GetString(4), reader.GetString(5)));
            }
            return (cursor, complete);
        }
    }

    internal static int SettlePathCapacity(IReadOnlyList<DeferredPlanning> ordinary) =>
        Math.Max(0, DeferredIntakePathCap - ordinary.Sum(row => row.Paths.Count));
}
