using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Massdex.SyncShark.Engine.Storage;

/// <summary>One owner of the synced_entry.keep column: the tails kept at or under a bound row ("" is the row itself),
/// the derived KEEP legs they project into the pair view, and enrollment/edit reconciliation on publication.
/// A leg (Row, Tail) is the persisted shape; no second type.</summary>
internal static class KeepRows
{
    // One row per kept tail so the projection is O(number of legs), not O(number of owners).
    private const string DeriveSql = """
        SELECT s.rowid, s.local_path, s.dropbox_path, j.value
        FROM synced_entry s, json_each(s.keep) j
        WHERE s.pair_id=(SELECT id FROM pair WHERE pair_id=$p) AND s.keep IS NOT NULL
        """;

    /// <summary>The configured KEEP legs projected into the pair view; each carries its owning row and relative tail.</summary>
    internal static async Task<List<Exclusion>> Derive(SqliteConnection db, SqliteTransaction? tx, string pairId, CancellationToken ct)
    {
        var legs = new List<Exclusion>();
        using var cmd = StorageCommand.Create(db, tx, DeriveSql, ("$p", pairId));
        using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var row = rd.GetInt64(0);
            var local = rd.GetString(1);
            var dropbox = rd.GetString(2);
            var tail = rd.GetString(3);
            legs.Add(new Exclusion(Side.Local, tail.Length == 0 ? local : PathNames.Child(local, tail), true) { KeptBy = (row, tail) });
            // A split row's two endpoints spell differently; list the Dropbox alias too.
            if (dropbox != local)
                legs.Add(new Exclusion(Side.Dropbox, tail.Length == 0 ? dropbox : PathNames.Child(dropbox, tail), true) { KeptBy = (row, tail) });
        }
        return legs;
    }

    // The nearest bound row whose local key is this rule's path or an ancestor, regardless of key split.
    private const string AttachSql = """
        SELECT s.rowid, s.local_path, s.dropbox_key_delta
        FROM synced_entry s
        WHERE s.pair_id=(SELECT id FROM pair WHERE pair_id=$p)
          AND pathkey(s.local_path) IN (SELECT value FROM json_each($keys))
        ORDER BY length(s.local_path) DESC
        LIMIT 1
        """;

    /// <summary>Attach a configured KEEP rule to the nearest equal-key bound row, or null when it stays a path rule
    /// (no equal-key owner, or a nearer split owner). The rule path is stored as a tail under that row.</summary>
    internal static async Task<(long Row, string Tail)?> Attach(SqliteConnection db, SqliteTransaction? tx, string pairId, Exclusion rule, CancellationToken ct)
    {
        var keys = PathNames.Ancestors(rule.Path).Append(rule.Path).Select(PathNames.Key).ToArray();
        long row;
        string rowPath;
        bool split;
        using (var find = StorageCommand.Create(db, tx, AttachSql, ("$p", pairId), ("$keys", JsonSerializer.Serialize(keys))))
        using (var rd = await find.ExecuteReaderAsync(ct))
        {
            if (!await rd.ReadAsync(ct)) return null;
            row = rd.GetInt64(0);
            rowPath = rd.GetString(1);
            split = !rd.IsDBNull(2);
        }
        if (split) return null;
        string tail;
        if (PathNames.Same(rule.Path, rowPath)) tail = string.Empty;
        else if (PathNames.IsSelfOrUnder(rule.Path, rowPath)) tail = rowPath.Length == 0 ? rule.Path : rule.Path[(rowPath.Length + 1)..];
        else return null;
        using var add = StorageCommand.Create(db, tx,
            "UPDATE synced_entry SET keep=json_insert(coalesce(keep,'[]'),'$[#]',$t) WHERE rowid=$r AND NOT EXISTS(SELECT 1 FROM json_each(coalesce(keep,'[]')) WHERE value=$t)",
            ("$r", row), ("$t", tail));
        await add.ExecuteNonQueryAsync(ct);
        return (row, tail);
    }

    private const string LegsSql = """
        SELECT s.rowid, j.value FROM synced_entry s, json_each(s.keep) j
        WHERE s.pair_id=(SELECT id FROM pair WHERE pair_id=$p) AND s.keep IS NOT NULL
        """;

    /// <summary>Every tail currently kept on a bound row of this pair.</summary>
    internal static async Task<List<(long Row, string Tail)>> Legs(SqliteConnection db, SqliteTransaction? tx, string pairId, CancellationToken ct)
    {
        var legs = new List<(long Row, string Tail)>();
        using var cmd = StorageCommand.Create(db, tx, LegsSql, ("$p", pairId));
        using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) legs.Add((rd.GetInt64(0), rd.GetString(1)));
        return legs;
    }

    /// <summary>Remove one tail from a row; an emptied array becomes NULL so the partial index skips it.</summary>
    internal static async Task Clear(SqliteConnection db, SqliteTransaction? tx, long row, string tail, CancellationToken ct)
    {
        using var cmd = StorageCommand.Create(db, tx, """
            UPDATE synced_entry SET keep =
              CASE WHEN (SELECT count(*) FROM json_each(keep) WHERE value<>$t)=0 THEN NULL
                   ELSE (SELECT json_group_array(value) FROM json_each(keep) WHERE value<>$t) END
            WHERE rowid=$r AND keep IS NOT NULL
            """, ("$r", row), ("$t", tail));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Reconcile the caller's edit against the rows: attach new KEEP rules, drop carried derived legs, and
    /// clear any leg the caller no longer lists (an Include). Returns only the rules to persist in def_json.</summary>
    internal static async Task<List<Exclusion>> Publish(SqliteConnection db, SqliteTransaction tx, string pairId,
        IReadOnlyList<Exclusion> viewed, CancellationToken ct)
    {
        var persisted = new List<Exclusion>(viewed.Count);
        var desired = new HashSet<(long Row, string Tail)>();
        foreach (var e in viewed)
        {
            if (e.KeptBy is { } kept) { desired.Add(kept); continue; } // carried derived leg; its row already holds it
            if (!e.Keep) { persisted.Add(e); continue; }
            if (await Attach(db, tx, pairId, e, ct) is { } leg) desired.Add(leg);
            else persisted.Add(e); // no equal-key row: stays a path rule
        }
        foreach (var leg in await Legs(db, tx, pairId, ct))
            if (!desired.Contains(leg)) await Clear(db, tx, leg.Row, leg.Tail, ct);
        return persisted;
    }
}
